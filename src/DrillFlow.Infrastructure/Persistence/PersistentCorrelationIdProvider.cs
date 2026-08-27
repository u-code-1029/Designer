using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Persistence;
using DrillFlow.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrillFlow.Infrastructure.Persistence;

/// <summary>
/// Allocates positive Int32 correlation IDs from blocks reserved in an atomically replaced
/// high-water-mark file. A sidecar file lock serializes block reservations made by multiple
/// processes using the same state path. Unused IDs in a reserved block are intentionally skipped
/// after a process restart so that an ID which might have escaped the process is never reused.
/// </summary>
public sealed class PersistentCorrelationIdProvider : ICorrelationIdProvider, IDisposable
{
    private const int ReservationBlockSize = 256;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly string _stateFilePath;
    private readonly string _lockFilePath;
    private readonly ILogger<PersistentCorrelationIdProvider> _logger;
    private readonly SemaphoreSlim _instanceGate = new(1, 1);
    private long _nextReservedId = 1;
    private long _reservedThrough;
    private bool _disposed;

    public PersistentCorrelationIdProvider(
        IOptions<CorrelationIdStoreOptions> options,
        ILogger<PersistentCorrelationIdProvider> logger)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var value = options.Value;
        var validation = new CorrelationIdStoreOptionsValidator().Validate(null, value);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                CorrelationIdStoreOptions.SectionName,
                typeof(CorrelationIdStoreOptions),
                validation.Failures);
        }

        _stateFilePath = value.StateFilePath;
        _lockFilePath = value.StateFilePath + ".lock";
    }

    public async Task<int> NextAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _instanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_nextReservedId > _reservedThrough)
            {
                await ReserveBlockAsync(cancellationToken).ConfigureAwait(false);
            }

            var next = checked((int)_nextReservedId);
            _nextReservedId++;
            _logger.LogTrace("Allocated equipment correlation ID {CorrelationId}.", next);
            return next;
        }
        finally
        {
            _instanceGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _instanceGate.Dispose();
    }

    private async Task ReserveBlockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_stateFilePath)!;
        Directory.CreateDirectory(directory);

        using (await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false))
        {
            var currentHighWaterMark = await ReadCurrentValueAsync(cancellationToken).ConfigureAwait(false);
            if (currentHighWaterMark == int.MaxValue)
            {
                throw new OverflowException(
                    "The positive Int32 correlation ID range has been exhausted. "
                    + "The state is not reset automatically because doing so could accept stale responses.");
            }

            var remaining = (long)int.MaxValue - currentHighWaterMark;
            var reservedCount = (int)Math.Min(ReservationBlockSize, remaining);
            var newHighWaterMark = checked(currentHighWaterMark + reservedCount);

            // The high-water mark must be durable before any ID in this block can escape the
            // provider. Cancellation is intentionally not observed between this write completing
            // and publishing the first in-memory ID.
            await WriteValueAtomicallyAsync(newHighWaterMark, cancellationToken).ConfigureAwait(false);
            _nextReservedId = (long)currentHighWaterMark + 1;
            _reservedThrough = newHighWaterMark;

            _logger.LogDebug(
                "Reserved equipment correlation ID block {FirstCorrelationId}-{LastCorrelationId}.",
                _nextReservedId,
                _reservedThrough);
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError == 32 || nativeError == 33;
    }

    private async Task<int> ReadCurrentValueAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return 0;
        }

        string text;
        using (var stream = new FileStream(
                   _stateFilePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.Asynchronous))
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
        {
            text = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var current)
            || current < 0)
        {
            throw new InvalidDataException(
                $"The correlation ID state file '{_stateFilePath}' is invalid. "
                + "It was not reset because doing so could reuse an ID.");
        }

        return current;
    }

    private async Task WriteValueAtomicallyAsync(int value, CancellationToken cancellationToken)
    {
        var tempPath = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
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

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFilePublisher.PublishCompletedTempFile(tempPath, _stateFilePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
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
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PersistentCorrelationIdProvider));
        }
    }
}
