namespace DrillFlow.Application.Communication;

/// <summary>
/// Receives non-blocking lifecycle notifications for the operator-facing equipment exchange
/// monitor. Implementations must return immediately and must never perform file I/O inline.
/// </summary>
public interface IEquipmentExchangeTraceSink
{
    void OnRequestPublished(
        string filePath,
        EquipmentRequestMessage request,
        int attempt);

    void OnResponseMatched(
        string filePath,
        EquipmentResponseMessage response);

    void OnExchangeStopped(
        string filePath,
        EquipmentRequestMessage request,
        string reason);
}
