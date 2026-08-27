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
    public EquipmentRequestSnapshot(
        int correlationId,
        string action,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (correlationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(correlationId));
        }

        CorrelationId = correlationId;
        Action = EquipmentActionNames.Normalize(action);
        var parameterCopy = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameters != null)
        {
            foreach (var pair in parameters)
            {
                parameterCopy.Add(pair.Key, pair.Value);
            }
        }

        Parameters = parameterCopy;
    }

    public int CorrelationId { get; }

    public string Action { get; }

    public IReadOnlyDictionary<string, object?> Parameters { get; }

}

public enum FrameResponseSimulationStatus
{
    Published,
    NoActiveRequest,
    ActiveRequestIsNotLive,
    ActiveRequestIsNotFrame = ActiveRequestIsNotLive,
    ActiveRequestChanged,
    ResponseAlreadyExists
}

/// <summary>
/// Describes a non-throwing outcome from the live frame commissioning responder. Expected file
/// races are results rather than failures so a polling loop can remain quiet and bounded.
/// </summary>
public sealed class FrameResponseSimulationResult
{
    public FrameResponseSimulationResult(
        FrameResponseSimulationStatus status,
        string responsePath,
        EquipmentRequestSnapshot? activeRequest = null)
    {
        Status = status;
        ResponsePath = responsePath ?? throw new ArgumentNullException(nameof(responsePath));
        ActiveRequest = activeRequest;
    }

    public FrameResponseSimulationStatus Status { get; }

    public string ResponsePath { get; }

    public EquipmentRequestSnapshot? ActiveRequest { get; }

    public bool IsPublished => Status == FrameResponseSimulationStatus.Published;
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
