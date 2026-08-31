using DrillFlow.Application.RealtimeVideo;

namespace DrillFlow.Desktop.Models;

public sealed class DesignerOptions
{
    public string Language { get; set; } = "Auto";

    public string Theme { get; set; } = ThemeSelection.System;

    public bool ValidateWorkflowOnEveryChange { get; set; } = true;

    /// <summary>
    /// Legacy per-user startup override. Deployment defaults come only from the top-level
    /// EquipmentCommunication section; a null value must not replace those defaults.
    /// </summary>
    public CommunicationSettings? Communication { get; set; }

    public RealtimeVideoOptions RealtimeVideo { get; set; } = new();
}
