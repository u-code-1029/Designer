using DrillFlow.Application.RealtimeVideo;

namespace DrillFlow.Desktop.Models;

public sealed class UserPreferences
{
    public string Language { get; set; } = "Auto";

    public string Theme { get; set; } = ThemeSelection.System;

    public bool ValidateWorkflowOnEveryChange { get; set; } = true;

    public CommunicationSettings Communication { get; set; } = new();

    public RealtimeVideoOptions RealtimeVideo { get; set; } = new();
}
