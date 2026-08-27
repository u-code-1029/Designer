using System;
using System.IO;
using DrillFlow.Application.Communication;

namespace DrillFlow.Desktop.Models;

public sealed class DesignerOptions
{
    public string Language { get; set; } = "Auto";

    public string Theme { get; set; } = ThemeSelection.System;

    public CommunicationSettings Communication { get; set; } = new();
}

public static class ThemeSelection
{
    public const string System = "System";

    public const string Light = "Light";

    public const string Dark = "Dark";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(value, Dark, StringComparison.OrdinalIgnoreCase))
        {
            return Dark;
        }

        return System;
    }
}

public sealed class CommunicationSettings
{
    public string ExchangeFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DrillFlow",
        "Exchange");

    public string RequestFileName { get; set; } = "request.xml";

    public string ResponseFileName { get; set; } = "response.xml";

    public string EquipmentRequestHandling { get; set; } = "RetainUntilOverwritten";

    public string AppRequestHandling { get; set; } = "DeleteAfterResponse";

    public string AppResponseHandling { get; set; } = "DeleteAfterRead";

    public int ResponseTimeoutMilliseconds { get; set; } = 30000;

    public bool RetryEnabled { get; set; }

    public int MaximumRetryCount { get; set; } = 1;

    public int RetryIntervalMilliseconds { get; set; } = 1000;

    public int PollingIntervalMilliseconds { get; set; } = 50;

    public int RequestPublishDelayMilliseconds { get; set; } = 100;

    internal void ApplyTo(EquipmentCommunicationOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.ExchangeDirectory = ExchangeFolder;
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
        options.ResponseTimeout = TimeSpan.FromMilliseconds(ResponseTimeoutMilliseconds);
        options.RetryEnabled = RetryEnabled;
        options.MaximumRetryCount = MaximumRetryCount;
        options.RetryDelay = TimeSpan.FromMilliseconds(RetryIntervalMilliseconds);
        options.PollingInterval = TimeSpan.FromMilliseconds(PollingIntervalMilliseconds);
        options.RequestPublishDelay = TimeSpan.FromMilliseconds(
            RequestPublishDelayMilliseconds);
    }

    public CommunicationSettings Clone() => (CommunicationSettings)MemberwiseClone();
}

public sealed class UserPreferences
{
    public string Language { get; set; } = "Auto";

    public string Theme { get; set; } = ThemeSelection.System;

    public CommunicationSettings Communication { get; set; } = new();
}
