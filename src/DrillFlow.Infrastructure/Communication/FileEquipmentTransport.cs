using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// Exchanges a single, atomically published request file for a matching response file. A fixed
/// sidecar opened with FileShare.None serializes the full exchange across processes and SMB
/// clients. Polling is deliberately the source of truth because it works for local folders and
/// UNC shares even when file-system change notifications are dropped.
/// </summary>
public sealed class FileEquipmentTransport : IEquipmentFileTransport, IDisposable
{
    private static readonly TimeSpan FrameCleanupWarningInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CanceledRequestCleanupTimeout = TimeSpan.FromSeconds(2);

    private readonly EquipmentCommunicationOptions _options;
    private readonly IEquipmentMessageCodec _codec;
    private readonly ILogger<FileEquipmentTransport> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly object _cleanupWarningGate = new();
    private readonly object _pendingCleanupGate = new();
    private readonly Dictionary<string, CleanupWarningState> _cleanupWarningStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PendingCanceledRequestCleanup> _pendingCanceledRequestCleanups =
        new();
    private int _disposeState;

    public FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger)
        : this(options, logger, new XmlTemplateEquipmentMessageCodec(), () => DateTime.UtcNow)
    {
    }

    public FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger,
        IEquipmentMessageCodec codec)
        : this(options, logger, codec, () => DateTime.UtcNow)
    {
    }

    internal FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger,
        Func<DateTime> utcNow)
        : this(options, logger, new XmlTemplateEquipmentMessageCodec(), utcNow)
    {
    }

    internal FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger,
        IEquipmentMessageCodec codec,
        Func<DateTime> utcNow)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _options = options.Value;

        var validation = new EquipmentCommunicationOptionsValidator().Validate(null, _options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                EquipmentCommunicationOptions.SectionName,
                typeof(EquipmentCommunicationOptions),
                validation.Failures);
        }
    }

    public Task<EquipmentResponseMessage> ExchangeAsync(
        EquipmentRequestMessage request,
        CancellationToken cancellationToken)
    {
        // ConfigureAwait(false) only controls continuations; it does not move synchronous work
        // performed before an incomplete await. Directory.CreateDirectory, FileStream construction,
        // metadata probes, and serializers can all synchronously wait on an unavailable SMB share.
        // Queue the entire operation so a WPF caller always gets control back before any of those
        // calls begin. Cancellation is intentionally consumed inside the worker: passing it to
        // Task.Run could cancel a queued operation without running the normal gate/lifetime checks.
        return Task.Run(() => ExchangeCoreAsync(request, cancellationToken), CancellationToken.None);
    }

    private async Task<EquipmentResponseMessage> ExchangeCoreAsync(
        EquipmentRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ThrowIfDisposed();
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? exchangeLock = null;
        string? publishedRequestPath = null;
        byte[]? publishedRequestPayload = null;
        var requestWasPublished = false;
        var exchangeGateOwnershipTransferred = false;
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_options.ExchangeDirectory);

            var exchangeLockPath = Path.Combine(
                _options.ExchangeDirectory,
                EquipmentCommunicationOptions.ExchangeLockFileName);
            exchangeLock = await AcquireExchangeLockAsync(exchangeLockPath, cancellationToken)
                .ConfigureAwait(false);

            // FileStream construction can outlive cancellation while the OS resolves an SMB
            // path. Re-check before any request/baseline work so a shutdown-abandoned open can
            // never come back later and publish a new command.
            cancellationToken.ThrowIfCancellationRequested();

            var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
            var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
            var serializedRequest = _codec.SerializeRequest(request);
            if (serializedRequest.Length > EquipmentMessageLimits.MaximumWirePayloadBytes)
            {
                throw new InvalidDataException(
                    $"The equipment request exceeds the {EquipmentMessageLimits.MaximumWirePayloadBytes} byte limit.");
            }
            publishedRequestPath = requestPath;
            publishedRequestPayload = serializedRequest;

            // In delete-after-read mode an existing request is never assumed to be stale and
            // overwritten. It may belong to a previous app run while the equipment still owns a
            // pending delete. Waiting for disappearance is the only safe first-run behavior.
            await EnsureRequestFileAbsentAsync(
                    requestPath,
                    EquipmentRequestDeletionWaitPhase.BeforeInitialPublish,
                    cancellationToken)
                .ConfigureAwait(false);

            // Hold both the in-process gate and the cross-process sidecar during this quiet
            // interval. This guarantees that every controller using the exchange directory sees
            // a gap between the preceding completed exchange and this request. The delay belongs
            // only to the first publication; retries already have their own RetryDelay. Capture
            // the retained response baseline afterwards so a late previous response written
            // during the interval can never be mistaken for this request's response.
            if (_options.RequestPublishDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.RequestPublishDelay, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A pre-existing retained response is only a baseline. Even if state was manually
            // rolled back, unchanged bytes are never accepted as the response to this exchange.
            var baselineResponse = await TryReadStableBytesAsync(responsePath, cancellationToken)
                .ConfigureAwait(false);

            var retryCount = _options.RetryEnabled ? _options.MaximumRetryCount : 0;
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    if (_options.RetryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                    }

                    await EnsureRequestFileAbsentAsync(
                            requestPath,
                            EquipmentRequestDeletionWaitPhase.BeforeRetry,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var responseBeforeRetry = await TryReadMatchingResponseOnceAsync(
                            responsePath,
                            request,
                            baselineResponse,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (responseBeforeRetry is not null)
                    {
                        return await CompleteResponseAsync(
                                requestPath,
                                responsePath,
                                responseBeforeRetry,
                                request,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                attempt++;

                await PublishRequestAsync(requestPath, serializedRequest, cancellationToken)
                    .ConfigureAwait(false);
                requestWasPublished = true;
                if (string.Equals(request.Action, EquipmentActionNames.Live, StringComparison.Ordinal))
                {
                    _logger.LogTrace(
                        "Published live frame request {CorrelationId} (attempt {Attempt}).",
                        request.CorrelationId,
                        attempt);
                }
                else
                {
                    _logger.LogInformation(
                        "Published equipment command {Command} with correlation ID {CorrelationId} "
                        + "(attempt {Attempt}).",
                        request.Action,
                        request.CorrelationId,
                        attempt);
                }

                var response = await WaitForMatchingResponseAsync(
                        responsePath,
                        request,
                        baselineResponse,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response is not null)
                {
                    return await CompleteResponseAsync(
                            requestPath,
                            responsePath,
                            response,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // A timeout is still a completion boundary. In delete-after-read mode the method
                // must not return (or retry) while the old pathname can be deleted later by the
                // equipment, otherwise a caller's next request could be lost.
                await EnsureRequestFileAbsentAsync(
                        requestPath,
                        EquipmentRequestDeletionWaitPhase.AfterResponseTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                var lateResponse = await TryReadMatchingResponseOnceAsync(
                        responsePath,
                        request,
                        baselineResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (lateResponse is not null)
                {
                    return await CompleteResponseAsync(
                            requestPath,
                            responsePath,
                            lateResponse,
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (attempt > retryCount)
                {
                    throw new EquipmentResponseTimeoutException(
                        request.CorrelationId,
                        attempt,
                        _options.ResponseTimeout);
                }

                _logger.LogWarning(
                    "Equipment response timed out for correlation ID {CorrelationId}; "
                    + "the identical request will be sent again ({Attempt}/{TotalAttempts}).",
                    request.CorrelationId,
                    attempt + 1,
                    retryCount + 1);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            if (requestWasPublished
                && exchangeLock is not null
                && publishedRequestPath is not null
                && publishedRequestPayload is not null)
            {
                var cleanupLock = exchangeLock;
                exchangeLock = null;
                exchangeGateOwnershipTransferred = true;
                ScheduleCanceledRequestCleanup(
                    cleanupLock,
                    publishedRequestPath,
                    publishedRequestPayload,
                    request.CorrelationId,
                    request.Action);
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            TryLogCanceledExchangeFailure(exception, request.CorrelationId, request.Action);
            throw new OperationCanceledException(
                "The equipment exchange was canceled.",
                exception,
                cancellationToken);
        }
        finally
        {
            try
            {
                exchangeLock?.Dispose();
            }
            finally
            {
                if (!exchangeGateOwnershipTransferred)
                {
                    _exchangeGate.Release();
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        // Canceled exchanges intentionally return before their ownership-safe file cleanup so
        // Stop, HFW changes, and exclusive Live commands remain responsive. Process shutdown is
        // the one boundary where that detached work must be joined: otherwise the CLR can exit
        // after the Live task has observed cancellation but before its request file is removed.
        // Each cleanup already owns a fixed two-second deadline. Wait only for its remaining
        // budget, never a fresh interval, so host disposal cannot extend the existing shutdown
        // budget. A blocked UNC/SMB system call may outlive this wait, but never blocks exit beyond
        // the same bounded deadline.
        DrainPendingCanceledRequestCleanups();

        // A network open can remain inside the operating system after application shutdown has
        // stopped awaiting it. Do not dispose the semaphore: the abandoned worker must still be
        // able to execute its finally/release path. SemaphoreSlim owns no OS handle unless
        // AvailableWaitHandle is requested, which this type never does.
    }

    private void ScheduleCanceledRequestCleanup(
        FileStream exchangeLock,
        string requestPath,
        byte[] expectedPayload,
        int correlationId,
        string command)
    {
        var cleanupDeadline = DateTime.UtcNow + CanceledRequestCleanupTimeout;
        try
        {
            var cleanupTask = Task.Run(
                () => CleanupCanceledRequestAsync(
                    exchangeLock,
                    requestPath,
                    expectedPayload,
                    correlationId,
                    command,
                    cleanupDeadline),
                CancellationToken.None);
            TrackCanceledRequestCleanup(cleanupTask, cleanupDeadline);
        }
        catch (Exception exception)
        {
            Exception cleanupFailure = exception;
            try
            {
                exchangeLock.Dispose();
            }
            catch (Exception disposeException)
            {
                cleanupFailure = new AggregateException(exception, disposeException);
            }
            finally
            {
                // ExchangeCore transferred both the sidecar and the in-process gate before
                // scheduling. A scheduler failure must release the gate exactly once.
                _exchangeGate.Release();
            }

            TryLogCanceledRequestCleanupFailure(
                cleanupFailure,
                requestPath,
                correlationId,
                command);
        }
    }

    private void TrackCanceledRequestCleanup(Task cleanupTask, DateTime deadline)
    {
        var pending = new PendingCanceledRequestCleanup(cleanupTask, deadline);
        lock (_pendingCleanupGate)
        {
            _pendingCanceledRequestCleanups.Add(pending);
        }

        _ = cleanupTask.ContinueWith(
            _ =>
            {
                lock (_pendingCleanupGate)
                {
                    _pendingCanceledRequestCleanups.Remove(pending);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DrainPendingCanceledRequestCleanups()
    {
        while (true)
        {
            PendingCanceledRequestCleanup[] pending;
            lock (_pendingCleanupGate)
            {
                // Do not rely on the ExecuteSynchronously continuation having run before this
                // thread is scheduled again. A completed cleanup can otherwise remain in the set
                // for one or more iterations and turn the shutdown drain into a tight spin.
                _pendingCanceledRequestCleanups.RemoveWhere(item => item.Task.IsCompleted);
                if (_pendingCanceledRequestCleanups.Count == 0)
                {
                    return;
                }

                pending = new PendingCanceledRequestCleanup[
                    _pendingCanceledRequestCleanups.Count];
                _pendingCanceledRequestCleanups.CopyTo(pending);
            }

            var latestDeadline = pending[0].Deadline;
            for (var index = 1; index < pending.Length; index++)
            {
                if (pending[index].Deadline > latestDeadline)
                {
                    latestDeadline = pending[index].Deadline;
                }
            }

            var remainingBudget = latestDeadline - DateTime.UtcNow;
            if (remainingBudget <= TimeSpan.Zero)
            {
                return;
            }

            if (remainingBudget > CanceledRequestCleanupTimeout)
            {
                // Wall-clock adjustment or concurrently registered work must never turn this
                // shutdown join into an interval longer than the established cleanup budget.
                remainingBudget = CanceledRequestCleanupTimeout;
            }

            var tasks = new Task[pending.Length];
            for (var index = 0; index < pending.Length; index++)
            {
                tasks[index] = pending[index].Task;
            }

            try
            {
                if (!Task.WaitAll(tasks, remainingBudget))
                {
                    return;
                }
            }
            catch (AggregateException exception)
            {
                // Cleanup owns full exception containment, but retain a defensive boundary here
                // so an unexpected task fault can never prevent application exit.
                TryLogCanceledRequestCleanupDrainFailure(exception.Flatten());
                return;
            }
            catch (Exception exception)
            {
                TryLogCanceledRequestCleanupDrainFailure(exception);
                return;
            }

            // A cleanup may have been registered while the snapshot was being joined. Re-check
            // under the gate and use that cleanup's original deadline as well.
        }
    }

    private void TryLogCanceledRequestCleanupDrainFailure(Exception exception)
    {
        try
        {
            _logger.LogWarning(
                exception,
                "Canceled equipment request cleanup could not be drained during shutdown; "
                + "application exit will continue.");
        }
        catch (Exception)
        {
            // Logging providers can already be disposing during host shutdown.
        }
    }

    private async Task CleanupCanceledRequestAsync(
        FileStream exchangeLock,
        string requestPath,
        byte[] expectedPayload,
        int correlationId,
        string command,
        DateTime deadline)
    {
        try
        {
            using (exchangeLock)
            {
                Exception? lastFailure = null;
                while (true)
                {
                    var remainingBudget = deadline - DateTime.UtcNow;
                    if (remainingBudget <= TimeSpan.Zero)
                    {
                        TryLogCanceledRequestCleanupFailure(
                            lastFailure ?? new TimeoutException(
                                "Canceled request cleanup exceeded its time budget."),
                            requestPath,
                            correlationId,
                            command);
                        return;
                    }

                    try
                    {
                        // Keep both the in-process gate and cross-process sidecar while verifying
                        // and deleting. A following run on this instance waits for cleanup instead
                        // of timing out against its own sidecar with a short ResponseTimeout.
                        byte[]? currentPayload;
                        using (var readBudget = new CancellationTokenSource())
                        {
                            readBudget.CancelAfter(remainingBudget);
                            try
                            {
                                currentPayload = await TryReadStableBytesAsync(
                                        requestPath,
                                        readBudget.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (readBudget.IsCancellationRequested)
                            {
                                TryLogCanceledRequestCleanupFailure(
                                    new TimeoutException(
                                        "Canceled request cleanup exceeded its time budget while "
                                        + "waiting for a stable file read."),
                                    requestPath,
                                    correlationId,
                                    command);
                                return;
                            }
                        }

                        // StableReadDelay is part of the fixed cleanup budget. Do not perform a
                        // late delete merely because an uninterruptible OS call returned after the
                        // deadline; that call is the only part .NET Framework cannot hard-bound.
                        if (DateTime.UtcNow >= deadline)
                        {
                            TryLogCanceledRequestCleanupFailure(
                                new TimeoutException(
                                    "Canceled request cleanup exceeded its time budget."),
                                requestPath,
                                correlationId,
                                command);
                            return;
                        }

                        if (currentPayload is not null)
                        {
                            if (!ByteArraysEqual(currentPayload, expectedPayload))
                            {
                                TryLogPreservedMismatchedCanceledRequest(
                                    requestPath,
                                    correlationId,
                                    command);
                                return;
                            }

                            File.Delete(requestPath);
                            TryLogCanceledRequestDeleted(requestPath, correlationId, command);
                            return;
                        }

                        var presence = GetRequestFilePresence(requestPath);
                        if (presence == RequestFilePresence.Absent)
                        {
                            return;
                        }

                        lastFailure = new IOException(
                            presence == RequestFilePresence.Present
                                ? "The canceled request exists but could not be read as a stable file."
                                : "The canceled request path could not be queried.");
                    }
                    catch (Exception exception) when (IsRecoverableFileCleanupFailure(exception))
                    {
                        lastFailure = exception;
                    }
                    catch (Exception exception)
                    {
                        TryLogCanceledRequestCleanupFailure(
                            exception,
                            requestPath,
                            correlationId,
                            command);
                        return;
                    }

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        TryLogCanceledRequestCleanupFailure(
                            lastFailure ?? new IOException("Canceled request cleanup timed out."),
                            requestPath,
                            correlationId,
                            command);
                        return;
                    }

                    var delay = remaining < _options.PollingInterval
                        ? remaining
                        : _options.PollingInterval;
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            // Keep the detached task fully observed even if disposal or a framework primitive
            // throws outside the inner file-operation guards.
            TryLogCanceledRequestCleanupFailure(
                exception,
                requestPath,
                correlationId,
                command);
        }
        finally
        {
            // ExchangeCore deliberately does not release this gate after transferring cleanup
            // ownership. Releasing here serializes an immediate next run without charging the
            // cleanup interval against that run's cross-process lock timeout.
            _exchangeGate.Release();
        }
    }

    private void TryLogCanceledExchangeFailure(
        Exception exception,
        int correlationId,
        string command)
    {
        try
        {
            _logger.LogDebug(
                exception,
                "Normalized a concurrent exchange failure to cancellation for command {Command} "
                + "with correlation ID {CorrelationId}.",
                command,
                correlationId);
        }
        catch (Exception)
        {
            // Logging providers can already be disposed during application shutdown.
        }
    }

    private void TryLogCanceledRequestDeleted(
        string requestPath,
        int correlationId,
        string command)
    {
        try
        {
            _logger.LogDebug(
                "Deleted canceled request {RequestPath} for command {Command} with correlation "
                + "ID {CorrelationId}.",
                requestPath,
                command,
                correlationId);
        }
        catch (Exception)
        {
            // Logging providers can already be disposed during application shutdown.
        }
    }

    private void TryLogPreservedMismatchedCanceledRequest(
        string requestPath,
        int correlationId,
        string command)
    {
        try
        {
            _logger.LogWarning(
                "Preserved request file {RequestPath} while cleaning canceled command {Command} "
                + "with correlation ID {CorrelationId} because its payload no longer matches the "
                + "request owned by that exchange.",
                requestPath,
                command,
                correlationId);
        }
        catch (Exception)
        {
            // Logging providers can already be disposed during application shutdown.
        }
    }

    private void TryLogCanceledRequestCleanupFailure(
        Exception exception,
        string requestPath,
        int correlationId,
        string command)
    {
        try
        {
            _logger.LogWarning(
                exception,
                "Could not delete canceled request {RequestPath} for command {Command} with "
                + "correlation ID {CorrelationId}. The workflow remains stopped.",
                requestPath,
                command,
                correlationId);
        }
        catch (Exception)
        {
            // Logging providers can already be disposed during application shutdown.
        }
    }

    private async Task<FileStream> AcquireExchangeLockAsync(
        string lockFilePath,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var loggedContention = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);

                if (loggedContention)
                {
                    _logger.LogDebug("Acquired equipment exchange lock {LockFilePath}.", lockFilePath);
                }

                return stream;
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                if (!loggedContention)
                {
                    loggedContention = true;
                    _logger.LogDebug(
                        "Waiting for another controller to release equipment exchange lock "
                        + "{LockFilePath}.",
                        lockFilePath);
                }

                var remaining = _options.ResponseTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new EquipmentExchangeLockTimeoutException(
                        lockFilePath,
                        _options.ResponseTimeout,
                        exception);
                }

                var delay = remaining < _options.PollingInterval
                    ? remaining
                    : _options.PollingInterval;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError == 32 || nativeError == 33;
    }

    private async Task PublishRequestAsync(
        string requestPath,
        byte[] serializedRequest,
        CancellationToken cancellationToken)
    {
        var tempPath = requestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(
                        serializedRequest,
                        0,
                        serializedRequest.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            var deadline = DateTime.UtcNow + _options.ResponseTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    AtomicFilePublisher.PublishCompletedTempFile(tempPath, requestPath);
                    return;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<EquipmentResponseMessage> CompleteResponseAsync(
        string requestPath,
        string responsePath,
        byte[] responseSnapshot,
        EquipmentRequestMessage request,
        CancellationToken cancellationToken)
    {
        await EnsureRequestFileAbsentAsync(
                requestPath,
                EquipmentRequestDeletionWaitPhase.AfterMatchingResponse,
                cancellationToken)
            .ConfigureAwait(false);

        // Stable polling has already captured and validated a matching response snapshot, so the
        // equipment has completed this request. Remove the completed request before materializing
        // the runtime result or deleting the captured response. This ordering is part of the file
        // handshake: the controller can observe request removal as the acknowledgement that its
        // response was detected. Cleanup remains deliberately best-effort; a share lock,
        // permission change, or equipment-side race must not discard a valid response or stop a
        // live-frame loop.
        TryDeleteCompletedRequest(requestPath, request.CorrelationId, request.Action);

        // Validation and materialization are deterministic for the immutable snapshot. Keeping
        // the second parse at this boundary makes the lifecycle explicit: detect a valid matching
        // response, acknowledge it by cleaning the request, then expose its values to callers.
        if (!_codec.TryDeserializeResponse(responseSnapshot, request, out var response)
            || response is null)
        {
            throw new InvalidDataException(
                "A validated equipment response snapshot could not be materialized.");
        }

        // The response object is fully materialized before its source file is removed. Consumers
        // therefore never depend on the response pathname remaining present after this method.
        if (_options.ApplicationResponseLifecycle == ApplicationResponseFileLifecycle.DeleteAfterRead)
        {
            await DeleteResponseAsync(
                    responsePath,
                    response.CorrelationId,
                    request.Action,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(request.Action, EquipmentActionNames.Live, StringComparison.Ordinal))
        {
            _logger.LogTrace(
                "Received live frame response {CorrelationId}.",
                response.CorrelationId);
        }
        else
        {
            _logger.LogInformation(
                "Received equipment response for correlation ID {CorrelationId}.",
                response.CorrelationId);
        }
        return response;
    }

    private void TryDeleteCompletedRequest(
        string requestPath,
        int correlationId,
        string requestCommand)
    {
        if (_options.ApplicationRequestLifecycle
            != ApplicationRequestFileLifecycle.DeleteAfterResponse)
        {
            return;
        }

        try
        {
            // File.Delete is already idempotent when the equipment deleted the path first.
            File.Delete(requestPath);
        }
        catch (Exception exception) when (IsRecoverableFileCleanupFailure(exception))
        {
            LogCleanupFailure(
                exception,
                "request",
                requestPath,
                correlationId,
                requestCommand);
        }
    }

    private async Task EnsureRequestFileAbsentAsync(
        string requestPath,
        EquipmentRequestDeletionWaitPhase phase,
        CancellationToken cancellationToken)
    {
        if (_options.EquipmentRequestLifecycle
            != EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)
        {
            return;
        }

        var elapsed = Stopwatch.StartNew();
        var loggedWait = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetRequestFilePresence(requestPath) == RequestFilePresence.Absent)
            {
                return;
            }

            if (!loggedWait)
            {
                loggedWait = true;
                _logger.LogDebug(
                    "Waiting for delete-after-read equipment to remove {RequestFilePath} "
                    + "during {WaitPhase}.",
                    requestPath,
                    phase);
            }

            var remaining = _options.ResponseTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new EquipmentRequestDeletionTimeoutException(
                    requestPath,
                    phase,
                    _options.ResponseTimeout);
            }

            var delay = remaining < _options.PollingInterval
                ? remaining
                : _options.PollingInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static RequestFilePresence GetRequestFilePresence(string requestPath)
    {
        try
        {
            File.GetAttributes(requestPath);
            return RequestFilePresence.Present;
        }
        catch (FileNotFoundException)
        {
            return RequestFilePresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return RequestFilePresence.Absent;
        }
        catch (IOException)
        {
            // A transient network/share failure is not evidence that the equipment has deleted
            // the file. Failing closed avoids publishing into an uncertain pathname.
            return RequestFilePresence.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return RequestFilePresence.Unknown;
        }
    }

    private async Task<byte[]?> TryReadMatchingResponseOnceAsync(
        string responsePath,
        EquipmentRequestMessage expectedRequest,
        byte[]? baselineResponse,
        CancellationToken cancellationToken)
    {
        var payload = await TryReadStableBytesAsync(responsePath, cancellationToken)
            .ConfigureAwait(false);
        if (payload is null || ByteArraysEqual(payload, baselineResponse))
        {
            return null;
        }

        if (_codec.TryDeserializeResponse(payload, expectedRequest, out _))
        {
            return payload;
        }

        return null;
    }

    private async Task<byte[]?> WaitForMatchingResponseAsync(
        string responsePath,
        EquipmentRequestMessage expectedRequest,
        byte[]? baselineResponse,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.ResponseTimeout;
        byte[]? lastRejectedPayload = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await TryReadStableBytesAsync(responsePath, cancellationToken)
                .ConfigureAwait(false);

            if (payload is not null
                && !ByteArraysEqual(payload, baselineResponse))
            {
                if (_codec.TryDeserializeResponse(payload, expectedRequest, out _))
                {
                    return payload;
                }

                if (!ByteArraysEqual(payload, lastRejectedPayload))
                {
                    LogRejectedResponse(responsePath, expectedRequest);
                    lastRejectedPayload = payload;
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < _options.PollingInterval
                ? remaining
                : _options.PollingInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private void LogRejectedResponse(
        string responsePath,
        EquipmentRequestMessage expectedRequest)
    {
        _logger.LogWarning(
            "Ignored stable response file {ResponsePath}: its XML template fields, action, or "
            + "correlation ID did not match pending {Action} request {CorrelationId}.",
            responsePath,
            expectedRequest.Action,
            expectedRequest.CorrelationId);
    }

    private async Task<byte[]?> TryReadStableBytesAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        FileSnapshot before;
        try
        {
            before = FileSnapshot.Capture(filePath);
            if (!before.Exists
                || before.Length > EquipmentMessageLimits.MaximumWirePayloadBytes)
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        await Task.Delay(_options.StableReadDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            var after = FileSnapshot.Capture(filePath);
            if (!after.Exists
                || !before.Equals(after)
                || after.Length > EquipmentMessageLimits.MaximumWirePayloadBytes)
            {
                return null;
            }

            using (var stream = new FileStream(
                       filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.Asynchronous))
            {
                var bytes = new byte[(int)after.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await stream.ReadAsync(
                            bytes,
                            offset,
                            bytes.Length - offset,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return null;
                    }

                    offset += read;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var final = FileSnapshot.Capture(filePath);
                return after.Equals(final) ? bytes : null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ByteArraysEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private async Task DeleteResponseAsync(
        string responsePath,
        int correlationId,
        string requestCommand,
        CancellationToken cancellationToken)
    {
        var maxDeleteWaitMilliseconds = Math.Min(
            2000d,
            Math.Max(100d, _options.PollingInterval.TotalMilliseconds * 20d));
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxDeleteWaitMilliseconds);
        Exception? lastException = null;

        while (true)
        {
            try
            {
                // File.Delete is idempotent if another participant removed the response first.
                File.Delete(responsePath);
                return;
            }
            catch (Exception exception) when (IsRecoverableFileCleanupFailure(exception))
            {
                lastException = exception;
            }

            // Live preview throughput is more important than retrying cleanup after the matching
            // frame has already been secured. A later exchange will still use the retained
            // response as its stale baseline, so one best-effort delete attempt is sufficient.
            if (string.Equals(requestCommand, EquipmentActionNames.Live, StringComparison.Ordinal))
            {
                LogCleanupFailure(
                    lastException!,
                    "response",
                    responsePath,
                    correlationId,
                    requestCommand);
                return;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
            {
                LogCleanupFailure(
                    lastException!,
                    "response",
                    responsePath,
                    correlationId,
                    requestCommand);
                return;
            }

            var delay = remaining < _options.PollingInterval
                ? remaining
                : _options.PollingInterval;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogCleanupFailure(
                    lastException!,
                    "response",
                    responsePath,
                    correlationId,
                    requestCommand);
                return;
            }
        }
    }

    private void LogCleanupFailure(
        Exception exception,
        string cleanupFileKind,
        string cleanupFilePath,
        int correlationId,
        string requestCommand)
    {
        if (string.Equals(requestCommand, EquipmentActionNames.Live, StringComparison.Ordinal))
        {
            if (!ShouldLogFrameCleanupWarning(
                    cleanupFileKind,
                    cleanupFilePath,
                    out var suppressedCount))
            {
                return;
            }

            // Frame acquisition can fail cleanup many times per minute. Keep this warning
            // stack-free and rate-limited while retaining the exception type and message needed
            // for diagnosis. Non-frame commands still log their full exception immediately.
            if (suppressedCount > 0)
            {
                _logger.LogWarning(
                    "Could not delete completed {CleanupFileKind} file {CleanupFilePath} for "
                    + "live frame correlation ID {CorrelationId}. {SuppressedCleanupWarningCount} "
                    + "repeated cleanup warning(s) were suppressed. Failure: "
                    + "{CleanupExceptionType}: {CleanupExceptionMessage}. The matching response "
                    + "remains successful and subsequent exchanges will still be attempted.",
                    cleanupFileKind,
                    cleanupFilePath,
                    correlationId,
                    suppressedCount,
                    exception.GetType().Name,
                    exception.Message);
            }
            else
            {
                _logger.LogWarning(
                    "Could not delete completed {CleanupFileKind} file {CleanupFilePath} for "
                    + "live frame correlation ID {CorrelationId}. Failure: "
                    + "{CleanupExceptionType}: {CleanupExceptionMessage}. The matching response "
                    + "remains successful and subsequent exchanges will still be attempted.",
                    cleanupFileKind,
                    cleanupFilePath,
                    correlationId,
                    exception.GetType().Name,
                    exception.Message);
            }

            return;
        }

        _logger.LogWarning(
            exception,
            "Could not delete completed {CleanupFileKind} file {CleanupFilePath} for command {Command} "
            + "with correlation ID {CorrelationId}. The matching response remains successful "
            + "and subsequent exchanges will still be attempted.",
            cleanupFileKind,
            cleanupFilePath,
            requestCommand,
            correlationId);
    }

    private bool ShouldLogFrameCleanupWarning(
        string cleanupFileKind,
        string cleanupFilePath,
        out int suppressedCount)
    {
        var key = cleanupFileKind + "\0" + cleanupFilePath;
        var now = _utcNow();

        lock (_cleanupWarningGate)
        {
            if (!_cleanupWarningStates.TryGetValue(key, out var state))
            {
                _cleanupWarningStates.Add(key, new CleanupWarningState(now));
                suppressedCount = 0;
                return true;
            }

            if (now < state.LastLoggedUtc
                || now - state.LastLoggedUtc >= FrameCleanupWarningInterval)
            {
                suppressedCount = state.SuppressedCount;
                state.LastLoggedUtc = now;
                state.SuppressedCount = 0;
                return true;
            }

            if (state.SuppressedCount < int.MaxValue)
            {
                state.SuppressedCount++;
            }

            suppressedCount = state.SuppressedCount;
            return false;
        }
    }

    private static bool IsRecoverableFileCleanupFailure(Exception exception)
    {
        return exception is IOException
               || exception is UnauthorizedAccessException
               || exception is System.Security.SecurityException
               || exception is NotSupportedException
               || exception is ArgumentException;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(nameof(FileEquipmentTransport));
        }
    }

    private readonly struct FileSnapshot : IEquatable<FileSnapshot>
    {
        private FileSnapshot(bool exists, long length, DateTime lastWriteTimeUtc)
        {
            Exists = exists;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public bool Exists { get; }

        public long Length { get; }

        private DateTime LastWriteTimeUtc { get; }

        public static FileSnapshot Capture(string filePath)
        {
            var info = new FileInfo(filePath);
            info.Refresh();
            return info.Exists
                ? new FileSnapshot(true, info.Length, info.LastWriteTimeUtc)
                : new FileSnapshot(false, 0, default);
        }

        public bool Equals(FileSnapshot other)
        {
            return Exists == other.Exists
                   && Length == other.Length
                   && LastWriteTimeUtc == other.LastWriteTimeUtc;
        }

        public override bool Equals(object? obj)
        {
            return obj is FileSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Exists.GetHashCode() * 397)
                       ^ Length.GetHashCode()
                       ^ LastWriteTimeUtc.GetHashCode();
            }
        }
    }

    private enum RequestFilePresence
    {
        Absent,
        Present,
        Unknown,
    }

    private sealed class CleanupWarningState
    {
        public CleanupWarningState(DateTime lastLoggedUtc)
        {
            LastLoggedUtc = lastLoggedUtc;
        }

        public DateTime LastLoggedUtc { get; set; }

        public int SuppressedCount { get; set; }
    }

    private sealed class PendingCanceledRequestCleanup
    {
        public PendingCanceledRequestCleanup(Task task, DateTime deadline)
        {
            Task = task;
            Deadline = deadline;
        }

        public Task Task { get; }

        public DateTime Deadline { get; }
    }

}
