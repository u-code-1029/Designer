using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    private readonly ILogger<FileEquipmentTransport> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly object _cleanupWarningGate = new();
    private readonly Dictionary<string, CleanupWarningState> _cleanupWarningStates =
        new(StringComparer.OrdinalIgnoreCase);
    private int _disposeState;

    public FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger)
        : this(options, logger, () => DateTime.UtcNow)
    {
    }

    internal FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger,
        Func<DateTime> utcNow)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        string? publishedRequestPayload = null;
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
            var serializedRequest = SerializeRequest(request);
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

            // A pre-existing retained response is only a baseline. Even if state was manually
            // rolled back, unchanged bytes are never accepted as the response to this exchange.
            var baselineResponse = await TryReadStableTextAsync(responsePath, cancellationToken)
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
                            request.Index,
                            baselineResponse,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (responseBeforeRetry is not null)
                    {
                        return await CompleteResponseAsync(
                                requestPath,
                                responsePath,
                                responseBeforeRetry,
                                request.Command,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                attempt++;

                await PublishRequestAsync(requestPath, serializedRequest, cancellationToken)
                    .ConfigureAwait(false);
                requestWasPublished = true;
                if (string.Equals(request.Command, "frame", StringComparison.Ordinal))
                {
                    _logger.LogTrace(
                        "Published live frame request {CorrelationId} (attempt {Attempt}).",
                        request.Index,
                        attempt);
                }
                else
                {
                    _logger.LogInformation(
                        "Published equipment command {Command} with correlation ID {CorrelationId} "
                        + "(attempt {Attempt}).",
                        request.Command,
                        request.Index,
                        attempt);
                }

                var response = await WaitForMatchingResponseAsync(
                        responsePath,
                        request.Index,
                        baselineResponse,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (response is not null)
                {
                    return await CompleteResponseAsync(
                            requestPath,
                            responsePath,
                            response,
                            request.Command,
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
                        request.Index,
                        baselineResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (lateResponse is not null)
                {
                    return await CompleteResponseAsync(
                            requestPath,
                            responsePath,
                            lateResponse,
                            request.Command,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (attempt > retryCount)
                {
                    throw new EquipmentResponseTimeoutException(
                        request.Index,
                        attempt,
                        _options.ResponseTimeout);
                }

                _logger.LogWarning(
                    "Equipment response timed out for correlation ID {CorrelationId}; "
                    + "the identical request will be sent again ({Attempt}/{TotalAttempts}).",
                    request.Index,
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
                    request.Index,
                    request.Command);
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            TryLogCanceledExchangeFailure(exception, request.Index, request.Command);
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
        Interlocked.Exchange(ref _disposeState, 1);

        // A network open can remain inside the operating system after application shutdown has
        // stopped awaiting it. Do not dispose the semaphore: the abandoned worker must still be
        // able to execute its finally/release path. SemaphoreSlim owns no OS handle unless
        // AvailableWaitHandle is requested, which this type never does.
    }

    private void ScheduleCanceledRequestCleanup(
        FileStream exchangeLock,
        string requestPath,
        string expectedPayload,
        int correlationId,
        string command)
    {
        var cleanupDeadline = DateTime.UtcNow + CanceledRequestCleanupTimeout;
        try
        {
            _ = Task.Run(
                () => CleanupCanceledRequestAsync(
                    exchangeLock,
                    requestPath,
                    expectedPayload,
                    correlationId,
                    command,
                    cleanupDeadline),
                CancellationToken.None);
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

    private async Task CleanupCanceledRequestAsync(
        FileStream exchangeLock,
        string requestPath,
        string expectedPayload,
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
                        string? currentPayload;
                        using (var readBudget = new CancellationTokenSource())
                        {
                            readBudget.CancelAfter(remainingBudget);
                            try
                            {
                                currentPayload = await TryReadStableTextAsync(
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
                            if (!string.Equals(
                                    currentPayload,
                                    expectedPayload,
                                    StringComparison.Ordinal))
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

    private static string SerializeRequest(EquipmentRequestMessage request)
    {
        var json = new JObject
        {
            ["index"] = request.Index,
            ["command"] = request.Command,
        };

        foreach (var parameter in request.Parameters)
        {
            json.Add(
                parameter.Key,
                parameter.Value is null ? JValue.CreateNull() : JToken.FromObject(parameter.Value));
        }

        var output = new StringBuilder();
        using (var stringWriter = new StringWriter(output, System.Globalization.CultureInfo.InvariantCulture))
        using (var jsonWriter = new ScientificNotationJsonTextWriter(stringWriter)
        {
            Formatting = Formatting.Indented,
        })
        {
            json.WriteTo(jsonWriter);
        }

        return output.ToString();
    }

    private async Task PublishRequestAsync(
        string requestPath,
        string serializedRequest,
        CancellationToken cancellationToken)
    {
        var tempPath = requestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(serializedRequest);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
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
        EquipmentResponseMessage response,
        string requestCommand,
        CancellationToken cancellationToken)
    {
        await EnsureRequestFileAbsentAsync(
                requestPath,
                EquipmentRequestDeletionWaitPhase.AfterMatchingResponse,
                cancellationToken)
            .ConfigureAwait(false);

        // Once a matching response exists, the equipment has completed this request and the app
        // may safely remove a retained request. Cleanup is deliberately best-effort: a share lock,
        // permission change, or an equipment-side race must not turn a successful command into a
        // failed exchange or stop a live-frame loop.
        TryDeleteCompletedRequest(requestPath, response.Index, requestCommand);

        if (_options.ApplicationResponseLifecycle == ApplicationResponseFileLifecycle.DeleteAfterRead)
        {
            await DeleteResponseAsync(
                    responsePath,
                    response.Index,
                    requestCommand,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.Equals(requestCommand, "frame", StringComparison.Ordinal))
        {
            _logger.LogTrace(
                "Received live frame response {CorrelationId}.",
                response.Index);
        }
        else
        {
            _logger.LogInformation(
                "Received equipment response for correlation ID {CorrelationId}.",
                response.Index);
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

    private async Task<EquipmentResponseMessage?> TryReadMatchingResponseOnceAsync(
        string responsePath,
        int expectedIndex,
        string? baselineResponse,
        CancellationToken cancellationToken)
    {
        var json = await TryReadStableTextAsync(responsePath, cancellationToken)
            .ConfigureAwait(false);
        return json is not null
               && !string.Equals(json, baselineResponse, StringComparison.Ordinal)
               && TryParseMatchingResponse(json, expectedIndex, out var response)
            ? response
            : null;
    }

    private async Task<EquipmentResponseMessage?> WaitForMatchingResponseAsync(
        string responsePath,
        int expectedIndex,
        string? baselineResponse,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.ResponseTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await TryReadStableTextAsync(responsePath, cancellationToken)
                .ConfigureAwait(false);

            if (json is not null
                && !string.Equals(json, baselineResponse, StringComparison.Ordinal)
                && TryParseMatchingResponse(json, expectedIndex, out var response))
            {
                return response;
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

    private async Task<string?> TryReadStableTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        FileSnapshot before;
        try
        {
            before = FileSnapshot.Capture(filePath);
            if (!before.Exists)
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
            if (!after.Exists || !before.Equals(after))
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
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
            {
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var final = FileSnapshot.Capture(filePath);
                return after.Equals(final) ? text : null;
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

    private static bool TryParseMatchingResponse(
        string json,
        int expectedIndex,
        out EquipmentResponseMessage? response)
    {
        response = null;
        JObject document;
        try
        {
            document = JObject.Parse(
                json,
                new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
        }
        catch (JsonException)
        {
            return false;
        }

        if (!HasValidResponsePropertyNames(document))
        {
            return false;
        }

        var indexToken = document["index"];
        var commandToken = document["command"];
        if (!TryReadPositiveIndex(indexToken, out var responseIndex)
            || responseIndex != expectedIndex
            || commandToken?.Type != JTokenType.String
            || !string.Equals(commandToken.Value<string>(), "return", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadFiniteNumber(document["stage_x"], out var stageX)
            || !TryReadFiniteNumber(document["stage_y"], out var stageY))
        {
            return false;
        }

        var imagePathToken = document["image_path"];
        if (imagePathToken != null
            && (imagePathToken.Type != JTokenType.String
                || !EquipmentResponseMessage.IsSupportedAbsoluteImagePath(
                    imagePathToken.Value<string>())))
        {
            return false;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.Properties())
        {
            if (string.Equals(property.Name, "index", StringComparison.Ordinal)
                || string.Equals(property.Name, "command", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(property.Name, "stage_x", StringComparison.Ordinal))
            {
                properties[property.Name] = stageX;
            }
            else if (string.Equals(property.Name, "stage_y", StringComparison.Ordinal))
            {
                properties[property.Name] = stageY;
            }
            else
            {
                properties[property.Name] = ConvertToken(property.Value);
            }
        }

        try
        {
            response = new EquipmentResponseMessage(expectedIndex, "return", properties);
            return true;
        }
        catch (ArgumentException)
        {
            response = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            response = null;
            return false;
        }
    }

    private static bool TryReadPositiveIndex(JToken? token, out int index)
    {
        index = 0;
        return token?.Type == JTokenType.Integer
               && int.TryParse(
                   token.ToString(Formatting.None),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out index)
               && index > 0;
    }

    private static bool HasValidResponsePropertyNames(JObject document)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.Properties())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !names.Add(property.Name))
            {
                return false;
            }

            if (string.Equals(property.Name, "iteration_path", StringComparison.OrdinalIgnoreCase)
                || IsNonCanonicalProperty(property.Name, "index")
                || IsNonCanonicalProperty(property.Name, "command")
                || IsNonCanonicalProperty(property.Name, "stage_x")
                || IsNonCanonicalProperty(property.Name, "stage_y")
                || IsNonCanonicalProperty(property.Name, "image_path"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNonCanonicalProperty(string propertyName, string canonicalName)
    {
        return string.Equals(propertyName, canonicalName, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(propertyName, canonicalName, StringComparison.Ordinal);
    }

    private static bool TryReadFiniteNumber(JToken? token, out double value)
    {
        value = 0d;
        if (token?.Type != JTokenType.Integer && token?.Type != JTokenType.Float)
        {
            return false;
        }

        try
        {
            value = token.Value<double>();
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static object? ConvertToken(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Null:
            case JTokenType.Undefined:
                return null;
            case JTokenType.Boolean:
                return token.Value<bool>();
            case JTokenType.Integer:
                {
                    var text = token.ToString(Formatting.None);
                    if (int.TryParse(
                            text,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var intValue))
                    {
                        return intValue;
                    }

                    if (long.TryParse(
                            text,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var longValue))
                    {
                        return longValue;
                    }

                    // Json.NET represents larger integer tokens with BigInteger. The expression
                    // value system intentionally has no arbitrary-precision numeric type, so keep
                    // the exact invariant digits as text rather than overflowing or rounding.
                    return text;
                }
            case JTokenType.Float:
                return token.Value<double>();
            case JTokenType.String:
                return token.Value<string>();
            case JTokenType.Date:
                return token.Value<DateTime>();
            case JTokenType.Guid:
                return token.Value<Guid>();
            case JTokenType.TimeSpan:
                return token.Value<TimeSpan>();
            case JTokenType.Array:
                {
                    var values = new List<object?>();
                    foreach (var child in token.Children())
                    {
                        values.Add(ConvertToken(child));
                    }

                    return new ReadOnlyCollection<object?>(values);
                }
            case JTokenType.Object:
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var property in ((JObject)token).Properties())
                    {
                        values[property.Name] = ConvertToken(property.Value);
                    }

                    return new ReadOnlyDictionary<string, object?>(values);
                }
            default:
                return token.ToString(Formatting.None, Array.Empty<JsonConverter>());
        }
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
            if (string.Equals(requestCommand, "frame", StringComparison.Ordinal))
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
        if (string.Equals(requestCommand, "frame", StringComparison.Ordinal))
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

        private long Length { get; }

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

    private sealed class ScientificNotationJsonTextWriter : JsonTextWriter
    {
        public ScientificNotationJsonTextWriter(TextWriter textWriter)
            : base(textWriter)
        {
        }

        public override void WriteValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new JsonWriterException("NaN and infinity are not valid equipment request numbers.");
            }

            WriteRawValue(FormatScientific(value, "0.#################E+0"));
        }

        public override void WriteValue(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new JsonWriterException("NaN and infinity are not valid equipment request numbers.");
            }

            WriteRawValue(FormatScientific(value, "0.########E+0"));
        }

        public override void WriteValue(decimal value)
        {
            WriteRawValue(FormatScientific(value, "0.############################E+0"));
        }

        private static string FormatScientific<T>(T value, string format)
            where T : IFormattable
        {
            var text = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            var exponentMarker = text.IndexOf('E');
            if (exponentMarker < 0)
            {
                return text;
            }

            var mantissa = text.Substring(0, exponentMarker);
            var exponent = int.Parse(
                text.Substring(exponentMarker + 1),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture);
            var exponentText = exponent > 0
                ? "+" + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return mantissa + "E" + exponentText;
        }
    }
}
