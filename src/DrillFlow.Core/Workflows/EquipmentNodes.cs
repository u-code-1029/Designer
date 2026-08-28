using System.Collections.Generic;

namespace DrillFlow.Core.Workflows
{
    public sealed class StageNode : WorkflowNode
    {
        public StageNode()
        {
            Key = "stage";
            DisplayName = "Move stage";
            MoveMode = ParameterBinding.Literal("relative");
            StageX = ParameterBinding.Literal("0E0");
            StageY = ParameterBinding.Literal("0E0");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Stage;

        public ParameterBinding MoveMode { get; set; }

        public ParameterBinding StageX { get; set; }

        public ParameterBinding StageY { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["move_mode"] = MoveMode,
                ["stage_x"] = StageX,
                ["stage_y"] = StageY
            };
        }
    }

    public sealed class CameraNode : WorkflowNode
    {
        public CameraNode()
        {
            Key = "camera";
            DisplayName = "Move camera";
            MoveMode = ParameterBinding.Literal("relative");
            CameraX = ParameterBinding.Literal("0E0");
            CameraY = ParameterBinding.Literal("0E0");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Camera;

        public ParameterBinding MoveMode { get; set; }

        public ParameterBinding CameraX { get; set; }

        public ParameterBinding CameraY { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["move_mode"] = MoveMode,
                ["camera_x"] = CameraX,
                ["camera_y"] = CameraY
            };
        }
    }

    public sealed class FocusNode : WorkflowNode
    {
        public FocusNode()
        {
            Key = "focus";
            DisplayName = "Auto focus";
            HorizontalFieldWidth = ParameterBinding.Literal("3.02E-6");
            Range = ParameterBinding.Literal("50E-6");
            Steps = ParameterBinding.Literal("13");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Focus;

        public ParameterBinding HorizontalFieldWidth { get; set; }

        public ParameterBinding Range { get; set; }

        public ParameterBinding Steps { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["hfw"] = HorizontalFieldWidth,
                ["range"] = Range,
                ["steps"] = Steps
            };
        }
    }

    public sealed class IntegrationNode : WorkflowNode
    {
        public IntegrationNode()
        {
            Key = "integration";
            DisplayName = "Capture integrated image";
            HorizontalFieldWidth = ParameterBinding.Literal("3.02E-6");
            FrameCount = ParameterBinding.Literal("8");
            ImagePath = ParameterBinding.Literal(@"C:\DrillFlow\Images\integration.png");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Integration;

        public ParameterBinding HorizontalFieldWidth { get; set; }

        public ParameterBinding FrameCount { get; set; }

        /// <summary>The absolute local or UNC pathname where equipment should save the image.</summary>
        public ParameterBinding ImagePath { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["hfw"] = HorizontalFieldWidth,
                ["frame_count"] = FrameCount,
                ["image_path"] = ImagePath
            };
        }
    }

    public sealed class LiveNode : WorkflowNode
    {
        public LiveNode()
        {
            Key = "live";
            DisplayName = "Capture live frame";
            HorizontalFieldWidth = ParameterBinding.Literal("1E-3");
            FrameCount = ParameterBinding.Literal("1");
            ImagePath = ParameterBinding.Literal(@"C:\DrillFlow\Images\live.png");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Live;

        public ParameterBinding HorizontalFieldWidth { get; set; }

        /// <summary>Live acquisition is one frame; validation requires this binding to evaluate to 1.</summary>
        public ParameterBinding FrameCount { get; set; }

        /// <summary>The absolute local or UNC pathname where equipment should save the frame.</summary>
        public ParameterBinding ImagePath { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["hfw"] = HorizontalFieldWidth,
                ["frame_count"] = FrameCount,
                ["image_path"] = ImagePath
            };
        }
    }

    public sealed class OmNode : WorkflowNode
    {
        public OmNode()
        {
            Key = "om";
            DisplayName = "Capture OM image";
            ImagePath = ParameterBinding.Literal(@"C:\DrillFlow\Images\om.bmp");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Om;

        /// <summary>The absolute local or UNC pathname where equipment should save the OM image.</summary>
        public ParameterBinding ImagePath { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["image_path"] = ImagePath
            };
        }
    }

    public sealed class LensNode : WorkflowNode
    {
        public LensNode()
        {
            Key = "lens";
            DisplayName = "Change lens";
            LensMode = ParameterBinding.Literal("no_change");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Lens;

        public ParameterBinding LensMode { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["lens_mode"] = LensMode
            };
        }
    }

    public sealed class AutoContrastBrightnessNode : WorkflowNode
    {
        public AutoContrastBrightnessNode()
        {
            Key = "acb";
            DisplayName = "Auto contrast and brightness";
            HorizontalFieldWidth = ParameterBinding.Literal("2.04E-6");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.AutoContrastBrightness;

        public ParameterBinding HorizontalFieldWidth { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["hfw"] = HorizontalFieldWidth
            };
        }
    }

    public sealed class AbortNode : WorkflowNode
    {
        private static readonly IReadOnlyDictionary<string, ParameterBinding> EmptyParameters =
            new Dictionary<string, ParameterBinding>();

        public AbortNode()
        {
            Key = "abort";
            DisplayName = "Abort equipment";
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Abort;

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return EmptyParameters;
        }
    }
}
