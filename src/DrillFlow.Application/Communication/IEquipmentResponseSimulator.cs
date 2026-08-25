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
        CancellationToken cancellationToken);

    /// <summary>Validates a user-edited response without writing it.</summary>
    ResponsePayloadValidationResult ValidatePayload(string payload);

    /// <summary>Atomically publishes a validated response to the configured response pathname.</summary>
    Task PublishAsync(string payload, CancellationToken cancellationToken);
}
