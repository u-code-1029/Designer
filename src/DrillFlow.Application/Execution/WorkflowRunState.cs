namespace DrillFlow.Application.Execution;

public enum WorkflowRunState
{
    Idle,
    Validating,
    Running,
    Paused,
    Stopping,
    Completed,
    Stopped,
    Faulted
}

public enum WorkflowNodeExecutionState
{
    Waiting,
    Running,
    Paused,
    Completed,
    Skipped,
    Stopped,
    Faulted
}

public enum DebugResumeMode
{
    Continue,
    Step
}
