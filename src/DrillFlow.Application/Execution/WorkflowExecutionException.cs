using System;

namespace DrillFlow.Application.Execution;

public sealed class WorkflowExecutionException : Exception
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
