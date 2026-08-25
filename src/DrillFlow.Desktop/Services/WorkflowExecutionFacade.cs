using System;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Execution;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Desktop.Services;

public sealed class WorkflowExecutionFacade : IWorkflowExecutionFacade
{
    private readonly IWorkflowRunner _runner;

    public WorkflowExecutionFacade(IWorkflowRunner runner)
    {
        _runner = runner;
    }

    public WorkflowRunState State => _runner.State;

    public WorkflowNode? CurrentNode => _runner.CurrentNode;

    public RunResultStore Results => _runner.Results;

    public event EventHandler<WorkflowRunStateChangedEventArgs>? RunStateChanged
    {
        add => _runner.RunStateChanged += value;
        remove => _runner.RunStateChanged -= value;
    }

    public event EventHandler<WorkflowNodeStateChangedEventArgs>? NodeStateChanged
    {
        add => _runner.NodeStateChanged += value;
        remove => _runner.NodeStateChanged -= value;
    }

    public Task RunAsync(WorkflowDocument document) => _runner.RunAsync(document, CancellationToken.None);

    public void Continue() => _runner.Continue();

    public void Step() => _runner.Step();

    public void RequestStop() => _runner.RequestStop();

    public void ForceStop() => _runner.ForceStop();
}
