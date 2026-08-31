using System;
using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Infrastructure.Communication.FileExchange;

/// <summary>
/// Reads only complete, writer-closed equipment files whose metadata remains unchanged around the
/// exact-length read.
/// </summary>
internal interface IStableEquipmentFileReader
{
    EquipmentFilePresence GetPresence(string filePath);

    Task<byte[]?> TryReadAsync(
        string filePath,
        TimeSpan stableReadDelay,
        int maximumPayloadBytes,
        CancellationToken cancellationToken);
}
