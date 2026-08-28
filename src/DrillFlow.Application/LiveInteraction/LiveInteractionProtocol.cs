using System;
using DrillFlow.Application.Communication;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>Canonical action, parameter, and range definitions used by Live Interaction.</summary>
public static class LiveInteractionProtocol
{
    public const string LiveAction = EquipmentActionNames.Live;

    public const string StageAction = EquipmentActionNames.Stage;

    public const string CameraAction = EquipmentActionNames.Camera;

    public const string FocusAction = EquipmentActionNames.Focus;

    public const string IntegrationAction = EquipmentActionNames.Integration;

    public const string LensAction = EquipmentActionNames.Lens;

    public const string AutoContrastBrightnessAction =
        EquipmentActionNames.AutoContrastBrightness;

    public const string HorizontalFieldWidthParameter = "hfw";

    public const string FrameCountParameter = "frame_count";

    public const string ImagePathParameter = "image_path";

    public const string MoveModeParameter = "move_mode";

    public const string StageXParameter = "stage_x";

    public const string StageYParameter = "stage_y";

    public const string CameraXParameter = "camera_x";

    public const string CameraYParameter = "camera_y";

    public const string FocusRangeParameter = "range";

    public const string FocusStepsParameter = "steps";

    public const string LensModeParameter = "lens_mode";

    public const string RelativeMoveMode = "relative";

    public const string AbsoluteMoveMode = "absolute";

    public const string Lens1Mode = "lens1";

    public const string Lens2Mode = "lens2";

    public const string NoLensChangeMode = "no_change";

    public const int LiveFrameCount = 1;

    public const int MaximumIntegrationFrameCount = 64;

    public const double MaximumHorizontalFieldWidthMetres = 2.4E-3d;

    public static bool IsMoveMode(string? value) =>
        string.Equals(value, RelativeMoveMode, StringComparison.Ordinal)
        || string.Equals(value, AbsoluteMoveMode, StringComparison.Ordinal);

    public static bool IsLensMode(string? value) =>
        string.Equals(value, Lens1Mode, StringComparison.Ordinal)
        || string.Equals(value, Lens2Mode, StringComparison.Ordinal)
        || string.Equals(value, NoLensChangeMode, StringComparison.Ordinal);

    public static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    public static bool IsValidHorizontalFieldWidth(double value) =>
        IsFinite(value) && value > 0d && value < MaximumHorizontalFieldWidthMetres;

    public static bool IsValidIntegrationFrameCount(int value) =>
        value > 0
        && value <= MaximumIntegrationFrameCount
        && (value & (value - 1)) == 0;
}
