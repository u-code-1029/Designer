using System;
using System.Collections.Generic;
using System.Linq;

namespace DrillFlow.Core.Validation
{
    public enum ValidationSeverity
    {
        Warning,
        Error
    }

    public sealed class ValidationIssue
    {
        public ValidationIssue(
            string code,
            string message,
            ValidationSeverity severity = ValidationSeverity.Error,
            Guid? nodeId = null,
            string? path = null)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Severity = severity;
            NodeId = nodeId;
            Path = path ?? string.Empty;
        }

        public string Code { get; }
        public string Message { get; }
        public ValidationSeverity Severity { get; }
        public Guid? NodeId { get; }
        public string Path { get; }
    }

    public sealed class WorkflowValidationResult
    {
        internal WorkflowValidationResult(IEnumerable<ValidationIssue> issues)
        {
            Issues = issues.ToArray();
        }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        public bool IsValid => Issues.All(x => x.Severity != ValidationSeverity.Error);
    }
}
