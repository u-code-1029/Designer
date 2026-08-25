using System;
using System.Collections.Generic;
using System.Linq;

namespace DrillFlow.Application.Communication;

public sealed class EquipmentResponseSimulationDraft
{
    public EquipmentResponseSimulationDraft(
        string payload,
        string responsePath,
        EquipmentRequestSnapshot? activeRequest)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        ResponsePath = responsePath ?? throw new ArgumentNullException(nameof(responsePath));
        ActiveRequest = activeRequest;
    }

    public string Payload { get; }

    public string ResponsePath { get; }

    public EquipmentRequestSnapshot? ActiveRequest { get; }
}

public sealed class EquipmentRequestSnapshot
{
    public EquipmentRequestSnapshot(int index, string command)
    {
        Index = index;
        Command = command ?? string.Empty;
    }

    public int Index { get; }

    public string Command { get; }
}

public sealed class ResponsePayloadValidationResult
{
    private ResponsePayloadValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<string> Errors { get; }

    public static ResponsePayloadValidationResult Success { get; }
        = new ResponsePayloadValidationResult(Array.Empty<string>());

    public static ResponsePayloadValidationResult Failure(params string[] errors)
    {
        return new ResponsePayloadValidationResult(
            (errors ?? Array.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray());
    }
}
