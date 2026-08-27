using System;
using System.Collections.Generic;

namespace DrillFlow.Core.Workflows
{
    public enum WorkflowNodeKind
    {
        Stage,
        Camera,
        Focus,
        Integration,
        Live,
        Abort,
        Http,
        Delay,
        Repeat,
        Conditional
    }

    public enum MoveCoordinateMode
    {
        Relative,
        Absolute
    }

    public abstract class WorkflowNode
    {
        protected WorkflowNode()
        {
            Id = Guid.NewGuid();
            Key = string.Empty;
            DisplayName = string.Empty;
            IsEnabled = true;
        }

        public Guid Id { get; set; }

        /// <summary>A stable, user-editable expression alias such as move_1.</summary>
        public string Key { get; set; }

        public string DisplayName { get; set; }

        public bool IsEnabled { get; set; }

        public bool HasBreakpoint { get; set; }

        public abstract WorkflowNodeKind Kind { get; }

        /// <summary>
        /// Returns the request/control-flow parameters by their expression and
        /// persistence names. The returned bindings retain the author's text.
        /// </summary>
        public abstract IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings();

        /// <summary>Enumerates nested nodes in execution/display order.</summary>
        public virtual IEnumerable<WorkflowNode> GetChildren()
        {
            yield break;
        }
    }
}
