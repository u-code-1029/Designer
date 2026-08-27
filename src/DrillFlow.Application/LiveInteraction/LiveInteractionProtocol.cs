namespace DrillFlow.Application.LiveInteraction;

/// <summary>Canonical field and command names for live equipment interaction requests.</summary>
public static class LiveInteractionProtocol
{
    public const string FrameCommand = "frame";

    public const string MoveCommand = "move";

    public const string CaptureCommand = "capture";

    public const string HorizontalFieldWidthParameter = "hfw";

    public const string MoveModeParameter = "move_mode";

    public const string MoveXParameter = "move_x";

    public const string MoveYParameter = "move_y";

    public const string RelativeMoveMode = "relative";
}
