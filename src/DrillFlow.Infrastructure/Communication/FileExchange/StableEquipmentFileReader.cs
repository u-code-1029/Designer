using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Infrastructure.Communication.FileExchange;

/// <summary>
/// Stable polling reader for local and UNC equipment exchange files. Sharing violations and
/// transient access failures are reported as an unavailable sample so the caller can poll again.
/// </summary>
internal sealed class StableEquipmentFileReader : IStableEquipmentFileReader
{
    public EquipmentFilePresence GetPresence(string filePath)
    {
        try
        {
            File.GetAttributes(filePath);
            return EquipmentFilePresence.Present;
        }
        catch (FileNotFoundException)
        {
            return EquipmentFilePresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return EquipmentFilePresence.Absent;
        }
        catch (IOException)
        {
            // A transient network/share failure is not evidence that the equipment has deleted
            // the file. Failing closed avoids publishing into an uncertain pathname.
            return EquipmentFilePresence.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return EquipmentFilePresence.Unknown;
        }
    }

    public async Task<byte[]?> TryReadAsync(
        string filePath,
        TimeSpan stableReadDelay,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        EquipmentFileSnapshot before;
        try
        {
            before = EquipmentFileSnapshot.Capture(filePath);
            if (!before.Exists || before.Length > maximumPayloadBytes)
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

        await Task.Delay(stableReadDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            var after = EquipmentFileSnapshot.Capture(filePath);
            if (!after.Exists
                || !before.Equals(after)
                || after.Length > maximumPayloadBytes)
            {
                return null;
            }

            using (var stream = new FileStream(
                       filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       // A response is considered publishable only after its writer has closed
                       // the file. Denying write sharing makes an in-progress local or SMB write
                       // fail this read attempt with a sharing violation, so polling can retry
                       // instead of parsing a temporarily stable partial payload.
                       FileShare.Read,
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
                var final = EquipmentFileSnapshot.Capture(filePath);
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
}
