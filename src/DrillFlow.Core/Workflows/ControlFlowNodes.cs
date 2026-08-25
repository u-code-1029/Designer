using System.Collections.Generic;

namespace DrillFlow.Core.Workflows
{
    public sealed class DelayNode : WorkflowNode
    {
        public DelayNode()
        {
            Key = "delay";
            DisplayName = "Delay";
            DurationMilliseconds = ParameterBinding.Literal("1000");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Delay;

        public ParameterBinding DurationMilliseconds { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["milliseconds"] = DurationMilliseconds
            };
        }
    }

    public sealed class RepeatNode : WorkflowNode
    {
        public RepeatNode()
        {
            Key = "repeat";
            DisplayName = "Repeat";
            Count = ParameterBinding.Literal("1");
            Body = new List<WorkflowNode>();
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Repeat;

        public ParameterBinding Count { get; set; }

        public List<WorkflowNode> Body { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["count"] = Count
            };
        }

        public override IEnumerable<WorkflowNode> GetChildren()
        {
            return Body ?? new List<WorkflowNode>();
        }
    }

    public enum ConditionalBranchKind
    {
        If,
        ElseIf,
        Else
    }

    public sealed class ConditionalBranch
    {
        public ConditionalBranch()
        {
            Id = System.Guid.NewGuid();
            Kind = ConditionalBranchKind.If;
            Condition = ParameterBinding.Literal("true");
            Body = new List<WorkflowNode>();
        }

        public System.Guid Id { get; set; }

        public ConditionalBranchKind Kind { get; set; }

        /// <summary>Null only for an Else branch.</summary>
        public ParameterBinding? Condition { get; set; }

        public List<WorkflowNode> Body { get; set; }
    }

    public sealed class ConditionalNode : WorkflowNode
    {
        private static readonly IReadOnlyDictionary<string, ParameterBinding> EmptyParameters =
            new Dictionary<string, ParameterBinding>();

        public ConditionalNode()
        {
            Key = "conditional";
            DisplayName = "If / Else";
            Branches = new List<ConditionalBranch>
            {
                new ConditionalBranch { Kind = ConditionalBranchKind.If }
            };
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Conditional;

        public List<ConditionalBranch> Branches { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return EmptyParameters;
        }

        public override IEnumerable<WorkflowNode> GetChildren()
        {
            if (Branches == null)
            {
                yield break;
            }

            foreach (var branch in Branches)
            {
                if (branch?.Body == null)
                {
                    continue;
                }

                foreach (var node in branch.Body)
                {
                    yield return node;
                }
            }
        }
    }
}
