using System;
using System.IO;

namespace DrillFlow.Application.Communication;

/// <summary>
/// Defines the file exchange contract shared with the equipment controller.
/// </summary>
public sealed class EquipmentCommunicationOptions
{
    private string _exchangeDirectory = string.Empty;
    private string _liveImageDirectory = string.Empty;

    public const string SectionName = "EquipmentCommunication";

    /// <summary>
    /// Fixed sidecar used to serialize every exchange that targets the same directory. Keeping
    /// the name stable lets Windows/SMB share-mode locking coordinate separate app processes and
    /// separate controller workstations.
    /// </summary>
    public const string ExchangeLockFileName = ".drillflow.exchange.lock";

    public const string DefaultLiveImageDirectoryName = ".drillflow-live";

    public string ExchangeDirectory
    {
        get => _exchangeDirectory;
        set => _exchangeDirectory = NormalizeExchangeDirectory(value);
    }

    /// <summary>
    /// Shared local or UNC directory proposed to the controller through each Live request's
    /// image_path. The actual filename remains correlation-specific so a delayed older frame
    /// cannot overwrite a newer request. When omitted, the existing exchange-directory
    /// subfolder is retained for backward compatibility.
    /// </summary>
    public string LiveImageDirectory
    {
        get => ResolveLiveImageDirectory(_exchangeDirectory, _liveImageDirectory);
        set => _liveImageDirectory = NormalizeExchangeDirectory(value);
    }

    /// <summary>
    /// Canonicalizes a local/UNC directory for Windows equipment messages. Windows file APIs
    /// accept forward slashes, but image_path is a strict backslash-based wire field.
    /// </summary>
    public static string NormalizeExchangeDirectory(string? value)
    {
        return (value ?? string.Empty).Trim().Replace('/', '\\');
    }

    public static string ResolveLiveImageDirectory(
        string? exchangeDirectory,
        string? configuredLiveImageDirectory)
    {
        var configured = NormalizeExchangeDirectory(configuredLiveImageDirectory);
        if (configured.Length > 0)
        {
            return configured;
        }

        var exchange = NormalizeExchangeDirectory(exchangeDirectory);
        return exchange.Length == 0
            ? string.Empty
            : Path.Combine(exchange, DefaultLiveImageDirectoryName);
    }

    public string RequestFileName { get; set; } = "request.xml";

    public string ResponseFileName { get; set; } = "response.xml";

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

    /// <summary>
    /// Delays the first publication of each logical equipment request. The quiet interval gives
    /// the controller time to transition from completing the previous response to watching for
    /// the next request. Retries use <see cref="RetryDelay"/> and do not apply this delay again.
    /// </summary>
    public TimeSpan RequestPublishDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan StableReadDelay { get; set; } = TimeSpan.FromMilliseconds(50);
}
