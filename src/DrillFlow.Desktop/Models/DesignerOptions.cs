using System;
using System.IO;

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

    public string RequestFileName { get; set; } = "request.json";

    public string ResponseFileName { get; set; } = "response.json";

    public string EquipmentRequestHandling { get; set; } = "RetainUntilOverwritten";

    public string AppRequestHandling { get; set; } = "DeleteAfterResponse";

    public string AppResponseHandling { get; set; } = "DeleteAfterRead";

    public int ResponseTimeoutMilliseconds { get; set; } = 30000;

    public bool RetryEnabled { get; set; }

    public int MaximumRetryCount { get; set; } = 1;

    public int RetryIntervalMilliseconds { get; set; } = 1000;

    public int PollingIntervalMilliseconds { get; set; } = 50;

    public CommunicationSettings Clone() => (CommunicationSettings)MemberwiseClone();
}

public sealed class UserPreferences
{
    public string Language { get; set; } = "Auto";

    public string Theme { get; set; } = ThemeSelection.System;

    public CommunicationSettings Communication { get; set; } = new();
}
