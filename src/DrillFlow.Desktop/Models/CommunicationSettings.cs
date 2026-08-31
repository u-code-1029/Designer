using System;
using System.IO;
using DrillFlow.Application.Communication;
using Newtonsoft.Json;

namespace DrillFlow.Desktop.Models;

public sealed class CommunicationSettings
{
    public string ExchangeFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DrillFlow",
        "Exchange");

    /// <summary>
    /// Shared folder used to build Live request image_path values. Blank values from older
    /// settings files intentionally resolve below the configured exchange folder.
    /// </summary>
    public string LiveImageFolder { get; set; } = string.Empty;

    public string RequestFileName { get; set; } = "request.xml";

    public string ResponseFileName { get; set; } = "response.xml";

    public string EquipmentRequestHandling { get; set; } = "RetainUntilOverwritten";

    public string AppRequestHandling { get; set; } = "DeleteAfterResponse";

    public string AppResponseHandling { get; set; } = "DeleteAfterRead";

    public double ResponseTimeoutSeconds { get; set; } = 30d;

    public bool RetryEnabled { get; set; }

    public int MaximumRetryCount { get; set; } = 1;

    public double RetryDelaySeconds { get; set; } = 1d;

    public double PollingIntervalSeconds { get; set; } = 0.05d;

    public double RequestPublishDelaySeconds { get; set; } = 0.1d;

    public double StableReadDelaySeconds { get; set; } = 0.05d;

    // Version 1 persisted the retry interval as an integer millisecond value. Keep this
    // deserialization-only alias so existing settings migrate without emitting both units.
    [JsonProperty("RetryIntervalMilliseconds")]
    private int LegacyRetryIntervalMilliseconds
    {
        set => RetryDelaySeconds = value / 1000d;
    }

    [JsonProperty("ResponseTimeoutMilliseconds")]
    private int LegacyResponseTimeoutMilliseconds
    {
        set => ResponseTimeoutSeconds = value / 1000d;
    }

    [JsonProperty("PollingIntervalMilliseconds")]
    private int LegacyPollingIntervalMilliseconds
    {
        set => PollingIntervalSeconds = value / 1000d;
    }

    [JsonProperty("RequestPublishDelayMilliseconds")]
    private int LegacyRequestPublishDelayMilliseconds
    {
        set => RequestPublishDelaySeconds = value / 1000d;
    }

    internal void ApplyTo(EquipmentCommunicationOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.ExchangeDirectory = ExchangeFolder;
        options.LiveImageDirectory = ResolveLiveImageFolder();
        options.RequestFileName = RequestFileName;
        options.ResponseFileName = ResponseFileName;
        options.EquipmentRequestLifecycle = Enum.TryParse<EquipmentRequestFileLifecycle>(
            EquipmentRequestHandling,
            true,
            out var requestLifecycle)
            ? requestLifecycle
            : (EquipmentRequestFileLifecycle)(-1);
        options.ApplicationRequestLifecycle = Enum.TryParse<ApplicationRequestFileLifecycle>(
            AppRequestHandling,
            true,
            out var applicationRequestLifecycle)
            ? applicationRequestLifecycle
            : (ApplicationRequestFileLifecycle)(-1);
        options.ApplicationResponseLifecycle = Enum.TryParse<ApplicationResponseFileLifecycle>(
            AppResponseHandling,
            true,
            out var responseLifecycle)
            ? responseLifecycle
            : (ApplicationResponseFileLifecycle)(-1);
        options.ResponseTimeout = TimeSpan.FromSeconds(ResponseTimeoutSeconds);
        options.RetryEnabled = RetryEnabled;
        options.MaximumRetryCount = MaximumRetryCount;
        options.RetryDelay = TimeSpan.FromSeconds(RetryDelaySeconds);
        options.PollingInterval = TimeSpan.FromSeconds(PollingIntervalSeconds);
        options.RequestPublishDelay = TimeSpan.FromSeconds(RequestPublishDelaySeconds);
        options.StableReadDelay = TimeSpan.FromSeconds(StableReadDelaySeconds);
    }

    internal string ResolveLiveImageFolder() =>
        EquipmentCommunicationOptions.ResolveLiveImageDirectory(
            ExchangeFolder,
            LiveImageFolder);

    public CommunicationSettings Clone() => (CommunicationSettings)MemberwiseClone();

    internal void MigrateLegacyDefaultFileNames()
    {
        // Version 1 used this exact pair as its built-in defaults. Preserve every custom name,
        // while ensuring an upgraded installation uses the XML wire contract on its first run.
        if (string.Equals(RequestFileName, "request.json", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ResponseFileName, "response.json", StringComparison.OrdinalIgnoreCase))
        {
            RequestFileName = "request.xml";
            ResponseFileName = "response.xml";
        }
    }

    public static CommunicationSettings FromOptions(EquipmentCommunicationOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new CommunicationSettings
        {
            ExchangeFolder = options.ExchangeDirectory,
            LiveImageFolder = options.LiveImageDirectory,
            RequestFileName = options.RequestFileName,
            ResponseFileName = options.ResponseFileName,
            EquipmentRequestHandling = options.EquipmentRequestLifecycle.ToString(),
            AppRequestHandling = options.ApplicationRequestLifecycle.ToString(),
            AppResponseHandling = options.ApplicationResponseLifecycle.ToString(),
            ResponseTimeoutSeconds = options.ResponseTimeout.TotalSeconds,
            RetryEnabled = options.RetryEnabled,
            MaximumRetryCount = options.MaximumRetryCount,
            RetryDelaySeconds = options.RetryDelay.TotalSeconds,
            PollingIntervalSeconds = options.PollingInterval.TotalSeconds,
            RequestPublishDelaySeconds = options.RequestPublishDelay.TotalSeconds,
            StableReadDelaySeconds = options.StableReadDelay.TotalSeconds
        };
    }
}
