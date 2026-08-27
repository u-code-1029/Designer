namespace DrillFlow.Application.Communication;

/// <summary>Hard safety limits shared by every equipment wire codec and file transport.</summary>
public static class EquipmentMessageLimits
{
    /// <summary>
    /// Maximum encoded request or response size. Equipment messages contain scalar metadata and
    /// paths rather than image bytes, so 4 MiB leaves ample contract headroom while preventing a
    /// malformed or hostile exchange file from driving an unbounded allocation.
    /// </summary>
    public const int MaximumWirePayloadBytes = 4 * 1024 * 1024;
}

/// <summary>
/// Converts logical messages to and from the equipment's fixed wire templates. File I/O,
/// publication, polling, retry, and lifecycle ownership remain transport concerns.
/// </summary>
public interface IEquipmentMessageCodec
{
    string WireFormat { get; }

    byte[] SerializeRequest(EquipmentRequestMessage request);

    byte[] SerializeResponse(EquipmentResponseMessage response);

    bool TryDeserializeRequest(byte[] payload, out EquipmentRequestMessage? request);

    bool TryDeserializeResponse(byte[] payload, out EquipmentResponseMessage? response);

    bool TryDeserializeResponse(
        byte[] payload,
        EquipmentRequestMessage expectedRequest,
        out EquipmentResponseMessage? response);
}
