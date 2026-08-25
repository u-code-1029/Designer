using System;
using System.IO;

namespace DrillFlow.Infrastructure.IO;

/// <summary>
/// Publishes a completely written temporary file. File.Replace failures caused by sharing,
/// locking, connectivity, or permissions are deliberately allowed to propagate to the caller;
/// delete-and-move is only used when the platform explicitly reports that replace semantics are
/// unsupported.
/// </summary>
internal static class AtomicFilePublisher
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorCallNotImplemented = 120;

    public static void PublishCompletedTempFile(string tempPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(tempPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(tempPath, destinationPath, null);
        }
        catch (PlatformNotSupportedException)
        {
            DeleteAndMoveCompletedTempFile(tempPath, destinationPath);
        }
        catch (NotSupportedException)
        {
            DeleteAndMoveCompletedTempFile(tempPath, destinationPath);
        }
        catch (IOException exception) when (IsKnownUnsupportedReplaceError(exception))
        {
            DeleteAndMoveCompletedTempFile(tempPath, destinationPath);
        }
    }

    internal static bool IsKnownUnsupportedReplaceError(IOException exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var nativeError = exception.HResult & 0xFFFF;
        return nativeError == ErrorInvalidFunction
               || nativeError == ErrorNotSupported
               || nativeError == ErrorCallNotImplemented;
    }

    private static void DeleteAndMoveCompletedTempFile(string tempPath, string destinationPath)
    {
        // The temporary file was fully flushed before this method was called. Consequently the
        // compatibility fallback can expose a short missing-file window, but never partial JSON.
        File.Delete(destinationPath);
        File.Move(tempPath, destinationPath);
    }
}

