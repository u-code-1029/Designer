using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DrillFlow.Desktop.Services;

public sealed class LiveCaptureSnapshotStore : ILiveCaptureSnapshotStore, IDisposable
{
    private const string SnapshotPrefix = "capture-";
    private const string SnapshotPattern = SnapshotPrefix + "*.snapshot";
    private const string StagingPattern = SnapshotPrefix + "*.tmp";
    private const int AcquisitionAttemptCount = 5;
    private const int RetryDelayMilliseconds = 100;
    private const int CopyBufferSize = 81920;

    private readonly object _sync = new object();
    private readonly HashSet<string> _ownedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<LiveCaptureSnapshotStore> _logger;
    private readonly string _snapshotDirectory;
    private bool _disposed;

    public LiveCaptureSnapshotStore(ILogger<LiveCaptureSnapshotStore> logger)
        : this(
            logger,
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrillFlow",
                "LiveCaptureSnapshots"))
    {
    }

    internal LiveCaptureSnapshotStore(
        ILogger<LiveCaptureSnapshotStore> logger,
        string snapshotDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshotDirectory = string.IsNullOrWhiteSpace(snapshotDirectory)
            ? throw new ArgumentException("A snapshot directory is required.", nameof(snapshotDirectory))
            : System.IO.Path.GetFullPath(snapshotDirectory);

        DeleteOrphanedSnapshots();
    }

    public async Task<LiveCaptureSnapshot> AcquireAsync(
        string sourceImagePath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var normalizedSource = NormalizePath(sourceImagePath);
        Exception? lastError = null;

        for (var attempt = 0; attempt < AcquisitionAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            Directory.CreateDirectory(_snapshotDirectory);

            var id = Guid.NewGuid().ToString("N");
            var stagingPath = System.IO.Path.Combine(
                _snapshotDirectory,
                SnapshotPrefix + id + ".tmp");
            var snapshotPath = System.IO.Path.Combine(
                _snapshotDirectory,
                SnapshotPrefix + id + ".snapshot");

            try
            {
                // FileStream construction can wait inside a Windows network provider for UNC or
                // mapped-drive paths. Keep that wait off the UI thread; cancellation is observed
                // between reads and retries even though Windows cannot always cancel an open call.
                await CopyStableSourceOffUiAsync(
                        normalizedSource,
                        stagingPath,
                        cancellationToken)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagingPath, snapshotPath);
                lock (_sync)
                {
                    ThrowIfDisposed();
                    _ownedPaths.Add(snapshotPath);
                }

                return new LiveCaptureSnapshot(snapshotPath, Release);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(stagingPath, "canceled capture acquisition");
                TryDelete(snapshotPath, "canceled capture acquisition");
                throw;
            }
            catch (Exception exception)
            {
                TryDelete(stagingPath, "capture acquisition retry");
                TryDelete(snapshotPath, "capture acquisition retry");
                if (!IsExpectedAcquisitionException(exception))
                {
                    throw;
                }

                lastError = exception;
            }

            if (attempt + 1 < AcquisitionAttemptCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            "The equipment capture could not be secured after bounded retries.",
            lastError);
    }

    public void Dispose()
    {
        string[] paths;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            paths = _ownedPaths.ToArray();
            _ownedPaths.Clear();
        }

        foreach (var path in paths)
        {
            TryDelete(path, "application shutdown");
        }

        TryDeleteDirectoryIfEmpty();
    }

    internal static bool IsConsistentSnapshot(
        long initialLength,
        DateTime initialWriteTimeUtc,
        long finalLength,
        DateTime finalWriteTimeUtc,
        long copiedLength)
    {
        return initialLength > 0
               && initialLength == finalLength
               && initialLength == copiedLength
               && initialWriteTimeUtc == finalWriteTimeUtc;
    }

    private static void CopyStableSource(
        string sourcePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(sourcePath);
        before.Refresh();
        if (!before.Exists || before.Length <= 0)
        {
            throw new IOException("The equipment capture does not exist or is empty.");
        }

        var initialLength = before.Length;
        var initialWriteTimeUtc = before.LastWriteTimeUtc;
        LiveImageSafetyLimits.ValidateEncodedByteLength(initialLength);
        long copiedLength = 0;
        using (var source = OpenSharedRead(sourcePath))
        using (var destination = new FileStream(
                   stagingPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   CopyBufferSize,
                   FileOptions.WriteThrough))
        {
            var buffer = new byte[CopyBufferSize];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (copiedLength + read > LiveImageSafetyLimits.MaximumEncodedBytes)
                {
                    throw new LiveImageLimitExceededException(
                        $"The equipment capture grew beyond the safe limit of {LiveImageSafetyLimits.MaximumEncodedBytes} bytes (64 MiB).");
                }

                destination.Write(buffer, 0, read);
                copiedLength += read;
            }

            destination.Flush(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var after = new FileInfo(sourcePath);
        after.Refresh();
        if (!after.Exists
            || !IsConsistentSnapshot(
                initialLength,
                initialWriteTimeUtc,
                after.Length,
                after.LastWriteTimeUtc,
                copiedLength))
        {
            throw new IOException("The equipment capture changed while it was being copied.");
        }
    }

    private async Task CopyStableSourceOffUiAsync(
        string sourcePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var copyTask = Task.Run(
            () => CopyStableSource(sourcePath, stagingPath, cancellationToken),
            CancellationToken.None);
        if (!cancellationToken.CanBeCanceled)
        {
            await copyTask.ConfigureAwait(false);
            return;
        }

        var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(copyTask, cancellationTask).ConfigureAwait(false) == copyTask)
        {
            await copyTask.ConfigureAwait(false);
            return;
        }

        // A Windows network provider may keep the worker inside FileStream.Open after the token
        // is canceled. Stop awaiting it so shutdown/UI can continue, but observe its completion
        // and remove any staging file after the provider eventually returns.
        _ = ObserveAbandonedCopyAsync(copyTask, stagingPath);
        throw new OperationCanceledException(cancellationToken);
    }

    private async Task ObserveAbandonedCopyAsync(Task copyTask, string stagingPath)
    {
        try
        {
            await copyTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original caller already observed cancellation; this prevents an unobserved fault.
        }
        finally
        {
            TryDelete(stagingPath, "canceled network capture acquisition");
            TryDeleteDirectoryIfEmpty();
        }
    }

    private void Release(string path)
    {
        lock (_sync)
        {
            _ownedPaths.Remove(path);
        }

        TryDelete(path, "completed capture operation");
        TryDeleteDirectoryIfEmpty();
    }

    private void DeleteOrphanedSnapshots()
    {
        if (!Directory.Exists(_snapshotDirectory))
        {
            return;
        }

        foreach (var pattern in new[] { SnapshotPattern, StagingPattern })
        {
            string[] paths;
            try
            {
                paths = Directory.GetFiles(
                    _snapshotDirectory,
                    pattern,
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Could not enumerate orphaned live capture snapshots in {SnapshotDirectory}.",
                    _snapshotDirectory);
                return;
            }

            foreach (var path in paths)
            {
                TryDelete(path, "application startup");
            }
        }

        TryDeleteDirectoryIfEmpty();
    }

    private void TryDelete(string path, string reason)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            _logger.LogWarning(
                exception,
                "Could not delete live capture snapshot {SnapshotPath} during {CleanupReason}.",
                path,
                reason);
        }
    }

    private void TryDeleteDirectoryIfEmpty()
    {
        try
        {
            if (Directory.Exists(_snapshotDirectory)
                && !Directory.EnumerateFileSystemEntries(_snapshotDirectory).Any())
            {
                Directory.Delete(_snapshotDirectory, false);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            _logger.LogDebug(
                exception,
                "Could not remove live capture snapshot directory {SnapshotDirectory}.",
                _snapshotDirectory);
        }
    }

    private static string NormalizePath(string sourceImagePath)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath))
        {
            throw new InvalidOperationException("The equipment did not return an image path.");
        }

        var trimmed = sourceImagePath.Trim();
        var path = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : trimmed;
        return System.IO.Path.GetFullPath(path);
    }

    private static FileStream OpenSharedRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferSize,
            FileOptions.SequentialScan);
    }

    private static bool IsExpectedAcquisitionException(Exception exception)
    {
        return exception is IOException
               || exception is UnauthorizedAccessException
               || exception is SecurityException;
    }

    private static bool IsExpectedFileSystemException(Exception exception)
    {
        return exception is IOException
               || exception is UnauthorizedAccessException
               || exception is SecurityException;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LiveCaptureSnapshotStore));
        }
    }
}
