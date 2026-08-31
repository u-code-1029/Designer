namespace DrillFlow.Infrastructure.Communication.FileExchange;

/// <summary>
/// Describes whether an equipment exchange path is known to exist. Transient file-system and
/// network failures are deliberately represented as <see cref="Unknown"/> rather than absence.
/// </summary>
internal enum EquipmentFilePresence
{
    Absent,
    Present,
    Unknown,
}
