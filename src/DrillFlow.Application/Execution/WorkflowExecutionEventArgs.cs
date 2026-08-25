using System;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Application.Execution;

public sealed class WorkflowRunStateChangedEventArgs : EventArgs
{
    public WorkflowRunStateChangedEventArgs(
        WorkflowRunState previousState,
        WorkflowRunState state,
        Guid? runId,
        string? message = null,
        Exception? exception = null)
    {
        PreviousState = previousState;
        State = state;
        RunId = runId;
        Message = message;
        Exception = exception;
    }

    public WorkflowRunState PreviousState { get; }

    public WorkflowRunState State { get; }

    public Guid? RunId { get; }

    public string? Message { get; }

    public Exception? Exception { get; }
}

public sealed class WorkflowNodeStateChangedEventArgs : EventArgs
{
    public WorkflowNodeStateChangedEventArgs(
        WorkflowNode node,
        WorkflowNodeExecutionState state,
        string iterationPath,
        ActionExecutionResult? result = null,
        Exception? exception = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        State = state;
        IterationPath = iterationPath ?? string.Empty;
        Result = result;
        Exception = exception;
    }

    public WorkflowNode Node { get; }

    public WorkflowNodeExecutionState State { get; }

    public string IterationPath { get; }

    public ActionExecutionResult? Result { get; }

    public Exception? Exception { get; }
}
