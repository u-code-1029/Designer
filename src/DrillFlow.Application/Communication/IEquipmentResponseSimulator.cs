using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Application.Communication;

/// <summary>
/// Creates and publishes a controller response for commissioning tests. The interface is payload
/// format neutral so a future XML equipment contract can replace the JSON implementation without
/// changing the WPF dialog.
/// </summary>
public interface IEquipmentResponseSimulator
{
    string PayloadFormat { get; }

    Task<EquipmentResponseSimulationDraft> CreateDraftAsync(
        WorkflowNode node,
        int? fallbackCorrelationId,
        CancellationToken cancellationToken,
        string? generatedImagePath = null);

    /// <summary>
    /// Reads the currently published equipment request, if it is complete enough to identify.
    /// This is used by the live commissioning controls so they create a test image only after a
    /// real frame request has been observed.
    /// </summary>
    Task<EquipmentRequestSnapshot?> GetActiveRequestAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a generated response only while <paramref name="expectedRequest"/> is still the
    /// active <c>frame</c> request. A response already written for that correlation ID wins and is
    /// never overwritten.
    /// </summary>
    Task<FrameResponseSimulationResult> TryPublishFrameResponseAsync(
        EquipmentRequestSnapshot expectedRequest,
        string generatedImagePath,
        CancellationToken cancellationToken);

    /// <summary>Validates a user-edited response without writing it.</summary>
    ResponsePayloadValidationResult ValidatePayload(string payload);

    /// <summary>Atomically publishes a validated response to the configured response pathname.</summary>
    Task PublishAsync(string payload, CancellationToken cancellationToken);
}
