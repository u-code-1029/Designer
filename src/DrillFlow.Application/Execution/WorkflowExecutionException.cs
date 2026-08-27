using System;
using DrillFlow.Core.Runtime;

namespace DrillFlow.Application.Execution;

public class WorkflowExecutionException : Exception
{
    public WorkflowExecutionException(string message)
        : base(message)
    {
    }

    public WorkflowExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class EquipmentActionFailedException : WorkflowExecutionException
{
    public EquipmentActionFailedException(string message, ActionExecutionResult result)
        : base(message)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ActionExecutionResult Result { get; }
}
