using System;
using System.Collections.Generic;

namespace DrillFlow.Core.Runtime
{
    /// <summary>
    /// A single execution result. Repeated actions produce one instance per
    /// iteration; none of these values are part of the persisted workflow.
    /// </summary>
    public sealed class ActionExecutionResult
    {
        public ActionExecutionResult()
        {
            ActionKey = string.Empty;
            IterationPath = new List<int>();
            Values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }

        public Guid ActionId { get; set; }

        public string ActionKey { get; set; }

        public int CorrelationId { get; set; }

        /// <summary>
        /// Zero-based indices of enclosing repeat iterations, from outermost
        /// to innermost.
        /// </summary>
        public List<int> IterationPath { get; set; }

        /// <summary>Response/control-flow result fields available to expressions.</summary>
        public Dictionary<string, object?> Values { get; set; }

        public DateTimeOffset CompletedAtUtc { get; set; }
    }
}
