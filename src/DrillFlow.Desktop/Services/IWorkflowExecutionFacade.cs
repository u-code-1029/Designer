using System;
using System.Threading.Tasks;
using DrillFlow.Application.Execution;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Desktop.Services;

public interface IWorkflowExecutionFacade
{
    WorkflowRunState State { get; }

    WorkflowNode? CurrentNode { get; }

    RunResultStore Results { get; }

    event EventHandler<WorkflowRunStateChangedEventArgs>? RunStateChanged;

    event EventHandler<WorkflowNodeStateChangedEventArgs>? NodeStateChanged;

    Task RunAsync(WorkflowDocument document);

    void Continue();

    void Step();

    void RequestStop();

    void ForceStop();
}
