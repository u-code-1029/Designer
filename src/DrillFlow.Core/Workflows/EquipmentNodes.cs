using System.Collections.Generic;

namespace DrillFlow.Core.Workflows
{
    public sealed class MoveNode : WorkflowNode
    {
        public MoveNode()
        {
            Key = "move";
            DisplayName = "Move drill head";
            MoveMode = ParameterBinding.Literal("relative");
            MoveX = ParameterBinding.Literal("0E0");
            MoveY = ParameterBinding.Literal("0E0");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Move;

        public ParameterBinding MoveMode { get; set; }

        public ParameterBinding MoveX { get; set; }

        public ParameterBinding MoveY { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["move_mode"] = MoveMode,
                ["move_x"] = MoveX,
                ["move_y"] = MoveY
            };
        }
    }

    public sealed class MeasureNode : WorkflowNode
    {
        public MeasureNode()
        {
            Key = "measure";
            DisplayName = "Measure distance";
            Thickness = ParameterBinding.Literal("1E-3");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Measure;

        public ParameterBinding Thickness { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["thickness"] = Thickness
            };
        }
    }

    public sealed class DrillNode : WorkflowNode
    {
        public DrillNode()
        {
            Key = "drill";
            DisplayName = "Drill";
            Thickness = ParameterBinding.Literal("1E-3");
            DrillResultPath = ParameterBinding.Literal(string.Empty);
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Drill;

        public ParameterBinding Thickness { get; set; }

        /// <summary>The destination path sent to the equipment.</summary>
        public ParameterBinding DrillResultPath { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["thickness"] = Thickness,
                ["drill_result_path"] = DrillResultPath
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
