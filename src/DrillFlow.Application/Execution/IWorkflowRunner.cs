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

    /// <summary>
    /// Runs one action subtree in the context of its complete document. Existing results remain
    /// in the current run so the selected action can reference and extend the current session.
    /// A result session is created only when none exists yet.
    /// </summary>
    Task RunSelectedAsync(
        WorkflowDocument document,
        Guid actionId,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused run until the next breakpoint.</summary>
    void Continue();

    /// <summary>Resumes a paused run for one executable node.</summary>
    void Step();

    /// <summary>
    /// Immediately stops the current local operation or equipment response wait and stops
    /// scheduling new nodes. No equipment abort command is generated. A file transport may
    /// best-effort remove the request owned by the canceled exchange.
    /// </summary>
    void RequestStop();

    /// <summary>
    /// Compatibility alias for <see cref="RequestStop"/>. Stop is immediate on its first request;
    /// this method never publishes an equipment abort command.
    /// </summary>
    void ForceStop();
}
