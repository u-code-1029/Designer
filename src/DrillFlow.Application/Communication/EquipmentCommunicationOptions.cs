using System;

namespace DrillFlow.Application.Communication;

/// <summary>
/// Defines the file exchange contract shared with the equipment controller.
/// </summary>
public sealed class EquipmentCommunicationOptions
{
    public const string SectionName = "EquipmentCommunication";

    /// <summary>
    /// Fixed sidecar used to serialize every exchange that targets the same directory. Keeping
    /// the name stable lets Windows/SMB share-mode locking coordinate separate app processes and
    /// separate controller workstations.
    /// </summary>
    public const string ExchangeLockFileName = ".drillflow.exchange.lock";

    public string ExchangeDirectory { get; set; } = string.Empty;

    public string RequestFileName { get; set; } = "request.json";

    public string ResponseFileName { get; set; } = "response.json";

    public EquipmentRequestFileLifecycle EquipmentRequestLifecycle { get; set; }
        = EquipmentRequestFileLifecycle.RetainUntilOverwritten;

    /// <summary>
    /// Controls application-side cleanup after a matching response has been received. This is
    /// separate from <see cref="EquipmentRequestLifecycle"/> so an installation can describe the
    /// equipment's ownership behavior and the application's post-response cleanup independently.
    /// </summary>
    public ApplicationRequestFileLifecycle ApplicationRequestLifecycle { get; set; }
        = ApplicationRequestFileLifecycle.DeleteAfterResponse;

    public ApplicationResponseFileLifecycle ApplicationResponseLifecycle { get; set; }
        = ApplicationResponseFileLifecycle.DeleteAfterRead;

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool RetryEnabled { get; set; }

    /// <summary>
    /// Number of re-sends after the initial request.
    /// </summary>
    public int MaximumRetryCount { get; set; } = 1;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan StableReadDelay { get; set; } = TimeSpan.FromMilliseconds(50);
}
