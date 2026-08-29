namespace DrillFlow.Desktop.Views;

internal interface IEquipmentPanelLayoutHost
{
    bool IsEquipmentPanelExpanded { get; set; }

    bool IsCommunicationRegionVisible { get; set; }

    bool SupportsValidationRegion { get; }

    bool IsValidationRegionVisible { get; set; }

    bool IsPreviewRegionVisible { get; set; }
}
