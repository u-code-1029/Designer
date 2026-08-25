using System;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Application.Execution;

public interface IWorkflowRunner
{
    WorkflowRunState State { get; }

    Guid? CurrentRunId { get; }

    WorkflowNode? CurrentNode { get; }

    RunResultStore Results { get; }

    event EventHandler<WorkflowRunStateChangedEventArgs>? RunStateChanged;

    event EventHandler<WorkflowNodeStateChangedEventArgs>? NodeStateChanged;

    /// <summary>Runs one document. Only one invocation may be active.</summary>
    Task RunAsync(WorkflowDocument document, CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused run until the next breakpoint.</summary>
    void Continue();

    /// <summary>Resumes a paused run for one executable node.</summary>
    void Step();

    /// <summary>
    /// Stops scheduling new nodes. An already-published equipment request is allowed to receive
    /// its response; no equipment abort command is generated.
    /// </summary>
    void RequestStop();

    /// <summary>
    /// Immediately cancels the active local operation or equipment exchange. This is intended for
    /// a second operator Stop request while <see cref="State"/> is already
    /// <see cref="WorkflowRunState.Stopping"/>. It never publishes an equipment abort command.
    /// A request file that was already published remains owned by the equipment.
    /// </summary>
    void ForceStop();
}
