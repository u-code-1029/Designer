using System.Collections.Generic;

namespace DrillFlow.Core.Workflows
{
    /// <summary>
    /// Performs an HTTP request inside the designer process. Unlike equipment
    /// nodes, this action never publishes or waits for files in the equipment
    /// exchange directory.
    /// </summary>
    public sealed class HttpActionNode : WorkflowNode
    {
        public HttpActionNode()
        {
            Key = "http";
            DisplayName = "HTTP request";
            Method = ParameterBinding.Literal("GET");
            Url = ParameterBinding.Literal("https://localhost/");
            Headers = ParameterBinding.Literal("{}");
            Body = ParameterBinding.Literal(string.Empty);
            TimeoutMilliseconds = ParameterBinding.Literal("30000");
        }

        public override WorkflowNodeKind Kind => WorkflowNodeKind.Http;

        public ParameterBinding Method { get; set; }

        public ParameterBinding Url { get; set; }

        /// <summary>
        /// A JSON object literal, or an expression that evaluates to an object.
        /// Header values may be strings, scalar values, or arrays of values.
        /// </summary>
        public ParameterBinding Headers { get; set; }

        /// <summary>
        /// A literal string is sent verbatim. An expression value that is an
        /// object or array is serialized as JSON by the HTTP executor.
        /// </summary>
        public ParameterBinding Body { get; set; }

        public ParameterBinding TimeoutMilliseconds { get; set; }

        public override IReadOnlyDictionary<string, ParameterBinding> GetParameterBindings()
        {
            return new Dictionary<string, ParameterBinding>
            {
                ["method"] = Method,
                ["url"] = Url,
                ["headers"] = Headers,
                ["body"] = Body,
                ["timeout_ms"] = TimeoutMilliseconds
            };
        }
    }
}
