using System;
using System.IO;

namespace DrillFlow.Infrastructure.Communication.FileExchange;

/// <summary>
/// Minimal metadata used to determine whether a file remained unchanged around an equipment
/// payload read.
/// </summary>
internal readonly struct EquipmentFileSnapshot : IEquatable<EquipmentFileSnapshot>
{
    private EquipmentFileSnapshot(bool exists, long length, DateTime lastWriteTimeUtc)
    {
        Exists = exists;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
    }

    public bool Exists { get; }

    public long Length { get; }

    private DateTime LastWriteTimeUtc { get; }

    public static EquipmentFileSnapshot Capture(string filePath)
    {
        var info = new FileInfo(filePath);
        info.Refresh();
        return info.Exists
            ? new EquipmentFileSnapshot(true, info.Length, info.LastWriteTimeUtc)
            : new EquipmentFileSnapshot(false, 0, default);
    }

    public bool Equals(EquipmentFileSnapshot other)
    {
        return Exists == other.Exists
               && Length == other.Length
               && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }

    public override bool Equals(object? obj)
    {
        return obj is EquipmentFileSnapshot other && Equals(other);
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
