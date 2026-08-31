using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Desktop.Services;

namespace DrillFlow.Desktop.ViewModels;

internal static class LiveImageFileLoader
{
    private const int MoveFileReplaceExisting = 0x1;
    private const int MoveFileWriteThrough = 0x8;
    private const int LoadAttemptCount = 5;
    private const int CaptureSnapshotAttemptCount = 3;
    private const int RetryDelayMilliseconds = 75;

    public static async Task<LiveImageDecodeResult> LoadAsync(
        string imagePath,
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        if (imageDecoder is null)
        {
            throw new ArgumentNullException(nameof(imageDecoder));
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < LoadAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await LoadOnceAsync(imagePath, imageDecoder, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableImageException(exception))
            {
                // A producer can expose the path before its final write/rename has completed.
                // Retrying both the read and WIC decode avoids accepting a stable-looking but
                // temporarily truncated file.
                lastError = exception;
            }

            if (attempt + 1 < LoadAttemptCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("The live image could not be loaded after bounded retries.", lastError);
    }

    public static async Task<LiveCaptureLoadResult> AcquireCaptureAsync(
        string sourceImagePath,
        ILiveCaptureSnapshotStore snapshotStore,
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        if (snapshotStore is null)
        {
            throw new ArgumentNullException(nameof(snapshotStore));
        }

        if (imageDecoder is null)
        {
            throw new ArgumentNullException(nameof(imageDecoder));
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < CaptureSnapshotAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LiveCaptureSnapshot? snapshot = null;
            try
            {
                snapshot = await snapshotStore
                    .AcquireAsync(sourceImagePath, cancellationToken)
                    .ConfigureAwait(false);
                var image = await LoadOnceAsync(
                        snapshot.Path,
                        imageDecoder,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new LiveCaptureLoadResult(snapshot, image);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                snapshot?.Dispose();
                throw;
            }
            catch (Exception exception) when (IsRetryableImageException(exception))
            {
                snapshot?.Dispose();
                lastError = exception;
            }
            catch
            {
                snapshot?.Dispose();
                throw;
            }

            if (attempt + 1 < CaptureSnapshotAttemptCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            "The equipment capture could not be secured and decoded after bounded retries.",
            lastError);
    }

    public static async Task CopyOriginalAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var normalizedSource = Path.GetFullPath(sourcePath);
        var normalizedDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var destinationDirectory = Path.GetDirectoryName(normalizedDestination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException("The capture destination has no parent directory.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            "." + Path.GetFileName(normalizedDestination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = OpenSharedRead(normalizedSource))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(normalizedDestination))
            {
                if (!MoveFileEx(
                        temporaryPath,
                        normalizedDestination,
                        MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not safely replace the existing captured image.");
                }
            }
            else
            {
                File.Move(temporaryPath, normalizedDestination);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
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
        catch (Exception)
        {
            // Best effort cleanup only; the original capture remains untouched.
        }
    }

    private static async Task<LiveImageDecodeResult> LoadOnceAsync(
        string imagePath,
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadImageBytesOffUiAsync(imagePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await imageDecoder.DecodeAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadImageBytesOffUiAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var readTask = Task.Run(
            () => ReadImageBytesOnce(imagePath, cancellationToken),
            CancellationToken.None);
        if (!cancellationToken.CanBeCanceled)
        {
            return await readTask.ConfigureAwait(false);
        }

        var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(readTask, cancellationTask).ConfigureAwait(false) == readTask)
        {
            return await readTask.ConfigureAwait(false);
        }

        _ = ObserveAbandonedReadAsync(readTask);
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task ObserveAbandonedReadAsync(Task<byte[]> readTask)
    {
        try
        {
            await readTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller already observed cancellation; avoid an unobserved worker exception.
        }
    }

    private static byte[] ReadImageBytesOnce(string imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new InvalidOperationException("The equipment did not return an image path.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = NormalizePath(imagePath);
        var before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists || before.Length <= 0)
        {
            throw new IOException("The live image does not exist or is empty.");
        }

        var initialLength = before.Length;
        var initialWriteTimeUtc = before.LastWriteTimeUtc;
        LiveImageSafetyLimits.ValidateEncodedByteLength(initialLength);
        var bytes = new byte[(int)initialLength];
        var copiedLength = 0;
        using (var source = OpenSharedRead(path))
        {
            while (copiedLength < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(
                    bytes,
                    copiedLength,
                    bytes.Length - copiedLength);
                if (read == 0)
                {
                    break;
                }

                copiedLength += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadByte() >= 0)
            {
                throw new IOException("The live image grew while it was being read.");
            }
        }

        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists
            || !LiveCaptureSnapshotStore.IsConsistentSnapshot(
                initialLength,
                initialWriteTimeUtc,
                after.Length,
                after.LastWriteTimeUtc,
                copiedLength))
        {
            throw new IOException("The live image changed while it was being read.");
        }

        return bytes;
    }

    private static bool IsRetryableImageException(Exception exception)
    {
        if (exception is ObjectDisposedException)
        {
            return false;
        }

        return exception is IOException
               || exception is UnauthorizedAccessException
               || exception is System.Security.SecurityException
               || exception is ArgumentException
               || exception is FormatException
               || exception is NotSupportedException
               || exception is InvalidOperationException
               || exception is COMException;
    }

    private static string NormalizePath(string imagePath)
    {
        var trimmed = imagePath.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : trimmed;
    }

    private static FileStream OpenSharedRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        int flags);
}
