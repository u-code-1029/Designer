using System;
using DrillFlow.Core.Validation;

namespace DrillFlow.Desktop.ViewModels;

public sealed class WorkflowValidationIssueViewModel
{
    public WorkflowValidationIssueViewModel(
        Guid? actionId,
        string actionAlias,
        string message,
        string path,
        ValidationSeverity severity)
    {
        ActionId = actionId;
        ActionAlias = actionAlias ?? string.Empty;
        Message = message ?? string.Empty;
        Path = path ?? string.Empty;
        Severity = severity;
    }

    public Guid? ActionId { get; }

    public string ActionAlias { get; }

    public string Message { get; }

    public string Path { get; }

    public ValidationSeverity Severity { get; }

    public bool IsError => Severity == ValidationSeverity.Error;
}
