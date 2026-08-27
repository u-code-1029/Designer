using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Http;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Validation;
using DrillFlow.Core.Workflows;
using Microsoft.Extensions.Logging;

namespace DrillFlow.Application.Execution;

public sealed class WorkflowRunner : IWorkflowRunner
{
    private const int CooperativeYieldInterval = 256;

    private readonly object _sync = new object();
    private readonly object _stateTransitionSync = new object();
    private readonly IEquipmentFileTransport _transport;
    private readonly IHttpActionExecutor _httpActions;
    private readonly ICorrelationIdProvider _correlationIds;
    private readonly ExpressionEngine _expressions;
    private readonly WorkflowValidator _validator;
    private readonly ILogger<WorkflowRunner> _logger;
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, object?>> _evaluatedParameters =
        new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();

    private WorkflowDocument? _document;
    private TaskCompletionSource<DebugResumeMode>? _pauseSource;
    private CancellationTokenSource? _localStopSource;
    private CancellationTokenSource? _forceStopSource;
    private bool _stopRequested;
    private bool _forceStopRequested;
    private bool _pauseBeforeNext;
    private bool _stepActive;
    private bool _runActive;
    private WorkflowRunState _state = WorkflowRunState.Idle;
    private WorkflowNode? _currentNode;

    public WorkflowRunner(
        IEquipmentFileTransport transport,
        IHttpActionExecutor httpActions,
        ICorrelationIdProvider correlationIds,
        ExpressionEngine expressions,
        WorkflowValidator validator,
        RunResultStore results,
        ILogger<WorkflowRunner> logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _httpActions = httpActions ?? throw new ArgumentNullException(nameof(httpActions));
        _correlationIds = correlationIds ?? throw new ArgumentNullException(nameof(correlationIds));
        _expressions = expressions ?? throw new ArgumentNullException(nameof(expressions));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        Results = results ?? throw new ArgumentNullException(nameof(results));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorkflowRunState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public Guid? CurrentRunId => Results.CurrentRunId;

    public WorkflowNode? CurrentNode
    {
        get
        {
            lock (_sync)
            {
                return _currentNode;
            }
        }
    }

    public RunResultStore Results { get; }

    public event EventHandler<WorkflowRunStateChangedEventArgs>? RunStateChanged;

    public event EventHandler<WorkflowNodeStateChangedEventArgs>? NodeStateChanged;

    public Task RunAsync(WorkflowDocument document, CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(document, selectedActionId: null, startNewRun: true, cancellationToken);
    }

    public Task RunSelectedAsync(
        WorkflowDocument document,
        Guid actionId,
        CancellationToken cancellationToken = default)
    {
        if (actionId == Guid.Empty)
        {
            throw new ArgumentException("A selected action must have a non-empty ID.", nameof(actionId));
        }

        return RunCoreAsync(document, actionId, startNewRun: false, cancellationToken);
    }

    private async Task RunCoreAsync(
        WorkflowDocument document,
        Guid? selectedActionId,
        bool startNewRun,
        CancellationToken cancellationToken)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var executionNodes = document.Nodes ?? new List<WorkflowNode>();
        if (selectedActionId is Guid actionId)
        {
            var selectedNode = document
                .EnumerateNodesDepthFirst()
                .FirstOrDefault(node => node.Id == actionId);
            if (selectedNode == null)
            {
                throw new ArgumentException(
                    $"The selected action '{actionId}' does not exist in the workflow document.",
                    nameof(selectedActionId));
            }

            executionNodes = new List<WorkflowNode> { selectedNode };
        }

        lock (_sync)
        {
            if (_runActive)
            {
                throw new InvalidOperationException("A workflow is already running.");
            }

            _runActive = true;
            _stopRequested = false;
            _forceStopRequested = false;
            _pauseBeforeNext = false;
            _stepActive = false;
            _document = document;
            _currentNode = null;
            if (startNewRun || Results.CurrentRunId == null)
            {
                _evaluatedParameters.Clear();
            }
            _localStopSource = new CancellationTokenSource();
            _forceStopSource = new CancellationTokenSource();
        }

        CancellationToken forceStopToken;
        lock (_sync)
        {
            forceStopToken = _forceStopSource!.Token;
        }

        using (var runCancellation =
               CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, forceStopToken))
        {
        try
        {
            ChangeState(WorkflowRunState.Validating, "Validating workflow.");
            var validation = _validator.Validate(document);
            var errors = validation.Issues
                .Where(issue => issue.Severity == ValidationSeverity.Error)
                .ToArray();
            if (errors.Length != 0)
            {
                var message = "Workflow validation failed: " +
                              string.Join("; ", errors.Take(5).Select(issue => issue.Message));
                throw new WorkflowExecutionException(message);
            }

            var runId = startNewRun || Results.CurrentRunId == null
                ? Results.StartNewRun()
                : Results.CurrentRunId.Value;
            if (selectedActionId is Guid selectedId)
            {
                _logger.LogInformation(
                    "Running selected action {ActionId} in workflow {WorkflowId} as continuing run {RunId}",
                    selectedId,
                    document.Id,
                    runId);
            }
            else
            {
                _logger.LogInformation("Starting workflow {WorkflowId} as run {RunId}", document.Id, runId);
            }
            ChangeState(WorkflowRunState.Running, "Workflow started.");

            var outcome = await ExecuteSequenceAsync(
                    executionNodes,
                    new List<int>(),
                    runCancellation.Token)
                .ConfigureAwait(false);

            if (outcome == SequenceOutcome.Continue)
            {
                ChangeState(WorkflowRunState.Completed, "Workflow completed.");
            }
            else
            {
                ChangeState(WorkflowRunState.Stopped,
                    outcome == SequenceOutcome.Abort ? "Abort action completed." : "Workflow stopped.");
            }
        }
        catch (EquipmentResponseTimeoutException exception) when (IsStopRequested())
        {
            _logger.LogWarning(
                exception,
                "The in-flight equipment action timed out while run {RunId} was stopping.",
                CurrentRunId);
            ChangeState(WorkflowRunState.Stopped, "Stopped after the current response timed out.");
        }
        catch (OperationCanceledException) when (IsForceStopRequested())
        {
            _logger.LogInformation(
                "Workflow run {RunId} was stopped immediately; no equipment abort command was published.",
                CurrentRunId);
            ChangeState(WorkflowRunState.Stopped, "Workflow stopped.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Workflow run {RunId} was cancelled by application shutdown.", CurrentRunId);
            ChangeState(WorkflowRunState.Stopped, "Workflow cancelled.");
        }
        catch (Exception exception)
        {
            var failedNode = CurrentNode;
            if (failedNode != null)
            {
                RaiseNodeState(failedNode, WorkflowNodeExecutionState.Faulted, string.Empty, null, exception);
            }

            _logger.LogError(exception, "Workflow run {RunId} failed.", CurrentRunId);
            ChangeState(WorkflowRunState.Faulted, exception.Message, exception);
            throw;
        }
        finally
        {
            CancellationTokenSource? localStopSource;
            CancellationTokenSource? forceStopSource;
            lock (_sync)
            {
                _currentNode = null;
                _pauseSource = null;
                _runActive = false;
                _document = null;
                _stepActive = false;
                _pauseBeforeNext = false;
                localStopSource = _localStopSource;
                _localStopSource = null;
                forceStopSource = _forceStopSource;
                _forceStopSource = null;
            }

            localStopSource?.Dispose();
            forceStopSource?.Dispose();
        }
        }
    }

    public void Continue()
    {
        Resume(DebugResumeMode.Continue);
    }

    public void Step()
    {
        Resume(DebugResumeMode.Step);
    }

    public void RequestStop()
    {
        TaskCompletionSource<DebugResumeMode>? pauseSource;
        CancellationTokenSource? localStopSource;
        CancellationTokenSource? forceStopSource;

        lock (_sync)
        {
            if (!_runActive || IsTerminal(_state))
            {
                return;
            }

            _stopRequested = true;
            _forceStopRequested = true;
            pauseSource = _pauseSource;
            localStopSource = _localStopSource;
            forceStopSource = _forceStopSource;
        }

        // Publish Stopping before cancellation can complete the run. ChangeState serializes
        // notifications and rejects a terminal-to-Stopping regression if completion won the race.
        ChangeState(WorkflowRunState.Stopping, "Stopping the current action immediately.");

        // Cancel outside the runner lock because callbacks may synchronously query runner state.
        TryCancel(localStopSource);
        TryCancel(forceStopSource);
        pauseSource?.TrySetCanceled();
    }

    public void ForceStop() => RequestStop();

    private async Task<SequenceOutcome> ExecuteSequenceAsync(
        IEnumerable<WorkflowNode> nodes,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes.Where(item => item != null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStopRequested())
            {
                return SequenceOutcome.Stop;
            }

            if (!node.IsEnabled)
            {
                RaiseNodeState(node, WorkflowNodeExecutionState.Skipped, FormatPath(iterationPath));
                continue;
            }

            bool shouldExecute;
            try
            {
                shouldExecute = await PauseBeforeNodeAsync(node, iterationPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A breakpoint is raised before SetCurrentNode/ExecuteNodeAsync. Without an
                // explicit transition here, an immediate Stop leaves the card permanently marked
                // Paused even though the run itself is already terminal.
                RaiseNodeState(node, WorkflowNodeExecutionState.Stopped, FormatPath(iterationPath));
                throw;
            }

            if (!shouldExecute)
            {
                RaiseNodeState(node, WorkflowNodeExecutionState.Stopped, FormatPath(iterationPath));
                return SequenceOutcome.Stop;
            }

            SetCurrentNode(node);
            RaiseNodeState(node, WorkflowNodeExecutionState.Running, FormatPath(iterationPath));

            SequenceOutcome outcome;
            try
            {
                outcome = await ExecuteNodeAsync(node, iterationPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RaiseNodeState(node, WorkflowNodeExecutionState.Stopped, FormatPath(iterationPath));
                throw;
            }
            catch (EquipmentActionFailedException exception)
            {
                RaiseNodeState(
                    node,
                    WorkflowNodeExecutionState.Faulted,
                    FormatPath(iterationPath),
                    exception.Result,
                    exception);
                throw;
            }
            catch (Exception exception)
            {
                RaiseNodeState(node, WorkflowNodeExecutionState.Faulted, FormatPath(iterationPath), null, exception);
                throw;
            }
            finally
            {
                SetCurrentNode(null);
            }

            if (outcome != SequenceOutcome.Continue)
            {
                return outcome;
            }

            if (IsStopRequested())
            {
                return SequenceOutcome.Stop;
            }
        }

        return SequenceOutcome.Continue;
    }

    private async Task<SequenceOutcome> ExecuteNodeAsync(
        WorkflowNode node,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        switch (node)
        {
            case StageNode stage:
                return await ExecuteEquipmentNodeAsync(
                        stage,
                        EquipmentActionNames.Stage,
                        EvaluateStage(stage),
                        iterationPath,
                        cancellationToken)
                    .ConfigureAwait(false);

            case CameraNode camera:
                return await ExecuteEquipmentNodeAsync(
                        camera,
                        EquipmentActionNames.Camera,
                        EvaluateCamera(camera),
                        iterationPath,
                        cancellationToken)
                    .ConfigureAwait(false);

            case FocusNode focus:
                return await ExecuteEquipmentNodeAsync(
                        focus,
                        EquipmentActionNames.Focus,
                        EvaluateFocus(focus),
                        iterationPath,
                        cancellationToken)
                    .ConfigureAwait(false);

            case IntegrationNode integration:
                return await ExecuteEquipmentNodeAsync(
                        integration,
                        EquipmentActionNames.Integration,
                        EvaluateIntegration(integration),
                        iterationPath,
                        cancellationToken)
                    .ConfigureAwait(false);

            case LiveNode live:
                return await ExecuteEquipmentNodeAsync(
                        live,
                        EquipmentActionNames.Live,
                        EvaluateLive(live),
                        iterationPath,
                        cancellationToken)
                    .ConfigureAwait(false);

            case AbortNode abort:
                await ExecuteEquipmentNodeAsync(
                        abort,
                        EquipmentActionNames.Abort,
                        EmptyParameters(),
                        iterationPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                return SequenceOutcome.Abort;

            case HttpActionNode http:
                return await ExecuteHttpActionAsync(http, iterationPath, cancellationToken)
                    .ConfigureAwait(false);

            case DelayNode delay:
                return await ExecuteDelayAsync(delay, iterationPath, cancellationToken).ConfigureAwait(false);

            case RepeatNode repeat:
                return await ExecuteRepeatAsync(repeat, iterationPath, cancellationToken).ConfigureAwait(false);

            case ConditionalNode conditional:
                return await ExecuteConditionalAsync(conditional, iterationPath, cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new WorkflowExecutionException($"Unsupported workflow node '{node.GetType().Name}'.");
        }
    }

    private async Task<SequenceOutcome> ExecuteEquipmentNodeAsync(
        WorkflowNode node,
        string action,
        IReadOnlyDictionary<string, object?> parameters,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        RememberParameters(node, parameters);
        var index = await _correlationIds.NextAsync(cancellationToken).ConfigureAwait(false);
        var request = new EquipmentRequestMessage(index, action, parameters);
        _logger.LogInformation(
            "Publishing equipment action {EquipmentAction} with correlation {CorrelationId} for workflow action {ActionKey}",
            action,
            index,
            node.Key);

        var exchange = _transport.ExchangeAsync(request, cancellationToken);
        var response = await AwaitEquipmentExchangeOrStopAsync(
                exchange,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.CorrelationId != request.CorrelationId)
        {
            throw new WorkflowExecutionException(
                $"Equipment response correlation ID {response.CorrelationId} does not match request "
                + $"correlation ID {request.CorrelationId}.");
        }

        if (!string.Equals(response.Action, request.Action, StringComparison.Ordinal))
        {
            throw new WorkflowExecutionException(
                $"Equipment response action '{response.Action}' does not match request action "
                + $"'{request.Action}'.");
        }

        var values = response.Properties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        values["type"] = response.Type;
        values["correlation_id"] = response.CorrelationId;
        values["action"] = response.Action;
        values["result"] = response.Result;
        var result = RecordResult(node, index, iterationPath, values);
        if (!response.IsSuccess)
        {
            throw new EquipmentActionFailedException(
                $"Equipment action '{response.Action}' failed with result 1 "
                + $"(correlation ID {response.CorrelationId}).",
                result);
        }

        RaiseNodeState(node, WorkflowNodeExecutionState.Completed, FormatPath(iterationPath), result);
        MarkStepComplete();
        return SequenceOutcome.Continue;
    }

    private async Task<EquipmentResponseMessage> AwaitEquipmentExchangeOrStopAsync(
        Task<EquipmentResponseMessage> exchange,
        EquipmentRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (exchange.IsCompleted)
        {
            // The response won before we installed the cancellation race. Preserve that normal
            // completion even if Stop is requested just before this continuation is scheduled.
            return await exchange.ConfigureAwait(false);
        }

        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellationSignal.TrySetResult(true)))
        {
            var winner = await Task.WhenAny(exchange, cancellationSignal.Task).ConfigureAwait(false);
            if (ReferenceEquals(winner, exchange))
            {
                // Task.WhenAny gives us the ordering boundary. Once the exchange wins, do not
                // retroactively turn its valid response into cancellation merely because Stop
                // arrives before this continuation runs.
                return await exchange.ConfigureAwait(false);
            }
        }

        // A custom or OS-blocked transport may not return promptly even though its token has been
        // canceled. The run must still become terminal on the first Stop. Observe the late task so
        // it cannot raise an unobserved exception; FileEquipmentTransport retains its own lock and
        // performs ownership-checked request cleanup before allowing another publisher through.
        _ = ObserveStoppedEquipmentExchangeAsync(exchange, request);
        throw new OperationCanceledException(cancellationToken);
    }

    private async Task ObserveStoppedEquipmentExchangeAsync(
        Task<EquipmentResponseMessage> exchange,
        EquipmentRequestMessage request)
    {
        try
        {
            await exchange.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected after an operator Stop.
        }
        catch (Exception exception)
        {
            try
            {
                _logger.LogWarning(
                    exception,
                    "The canceled equipment exchange for action {EquipmentAction} with correlation ID "
                    + "{CorrelationId} completed late.",
                    request.Action,
                    request.CorrelationId);
            }
            catch (Exception)
            {
                // Host shutdown can dispose logging providers before a blocked OS call returns.
            }
        }
    }

    private async Task<SequenceOutcome> ExecuteDelayAsync(
        DelayNode node,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        var context = CreateExpressionContext();
        var milliseconds = ParameterValueValidator.GetDelayMilliseconds(
            _expressions.Evaluate(node.DurationMilliseconds, context));
        var parameters = new Dictionary<string, object?> { ["milliseconds"] = milliseconds };
        RememberParameters(node, parameters);

        CancellationToken localStopToken;
        lock (_sync)
        {
            localStopToken = _localStopSource?.Token ?? CancellationToken.None;
        }

        using (var delayCancellation =
               CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localStopToken))
        {
            try
            {
                await Task.Delay(milliseconds, delayCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                localStopToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                RaiseNodeState(node, WorkflowNodeExecutionState.Stopped, FormatPath(iterationPath));
                return SequenceOutcome.Stop;
            }
        }

        var result = RecordResult(
            node,
            0,
            iterationPath,
            new Dictionary<string, object?> { ["elapsed_milliseconds"] = milliseconds });
        RaiseNodeState(node, WorkflowNodeExecutionState.Completed, FormatPath(iterationPath), result);
        MarkStepComplete();
        return SequenceOutcome.Continue;
    }

    private async Task<SequenceOutcome> ExecuteHttpActionAsync(
        HttpActionNode node,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        var context = CreateExpressionContext();
        var method = ParameterValueValidator.GetHttpMethod(_expressions.Evaluate(node.Method, context));
        var url = ParameterValueValidator.GetHttpUrl(_expressions.Evaluate(node.Url, context));
        var headers = ParameterValueValidator.GetHttpHeaders(_expressions.Evaluate(node.Headers, context));
        var body = ParameterValueValidator.GetHttpBody(_expressions.Evaluate(node.Body, context));
        var timeoutMilliseconds = ParameterValueValidator.GetHttpTimeoutMilliseconds(
            _expressions.Evaluate(node.TimeoutMilliseconds, context));
        var parameters = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["url"] = url,
            ["headers"] = headers,
            ["body"] = body,
            ["timeout_ms"] = timeoutMilliseconds
        };
        RememberParameters(node, parameters);

        var request = new HttpActionRequest(
            method,
            url,
            headers,
            body,
            TimeSpan.FromMilliseconds(timeoutMilliseconds));

        // IHttpActionExecutor implementations normally become asynchronous at SendAsync, but a
        // custom executor (or response-body reader) is allowed to do synchronous work first and
        // may ignore cancellation afterward. Start the entire call off the WPF caller and race
        // its returned task with the run token so the first Stop is always terminal promptly.
        var execution = Task.Run(
            () => _httpActions.ExecuteAsync(request, cancellationToken),
            CancellationToken.None);
        var response = await AwaitHttpActionOrStopAsync(execution, request, cancellationToken)
            .ConfigureAwait(false);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status_code"] = response.StatusCode,
            ["is_success"] = response.IsSuccessStatusCode,
            ["reason_phrase"] = response.ReasonPhrase,
            ["headers"] = response.Headers,
            ["body_text"] = response.Body,
            ["content_type"] = response.ContentType,
            ["json"] = response.Json
        };
        var result = RecordResult(node, 0, iterationPath, values);
        RaiseNodeState(node, WorkflowNodeExecutionState.Completed, FormatPath(iterationPath), result);
        MarkStepComplete();
        return SequenceOutcome.Continue;
    }

    private async Task<HttpActionResponse> AwaitHttpActionOrStopAsync(
        Task<HttpActionResponse> execution,
        HttpActionRequest request,
        CancellationToken cancellationToken)
    {
        if (execution.IsCompleted)
        {
            return await execution.ConfigureAwait(false);
        }

        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellationSignal.TrySetResult(true)))
        {
            var winner = await Task.WhenAny(execution, cancellationSignal.Task).ConfigureAwait(false);
            if (ReferenceEquals(winner, execution))
            {
                return await execution.ConfigureAwait(false);
            }
        }

        _ = ObserveStoppedHttpActionAsync(execution, request);
        throw new OperationCanceledException(cancellationToken);
    }

    private async Task ObserveStoppedHttpActionAsync(
        Task<HttpActionResponse> execution,
        HttpActionRequest request)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the HTTP stack honors the operator Stop.
        }
        catch (Exception exception)
        {
            try
            {
                _logger.LogWarning(
                    "The canceled designer HTTP action {Method} {Url} completed late with "
                    + "{ExceptionType}.",
                    request.Method,
                    GetSafeHttpLogUrl(request.Url),
                    exception.GetType().FullName ?? exception.GetType().Name);
            }
            catch (Exception)
            {
                // Host shutdown can dispose logging providers before a blocked task returns.
            }
        }
    }

    private static string GetSafeHttpLogUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "<invalid-url>";
        }

        // Uri.GetLeftPart(UriPartial.Path) can retain user-info from the authority. Rebuild the
        // URI explicitly so late-task diagnostics never expose credentials, query tokens, or a
        // fragment while still identifying the host/path that needs investigation.
        var safe = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return safe.Uri.GetLeftPart(UriPartial.Path);
    }

    private async Task<SequenceOutcome> ExecuteRepeatAsync(
        RepeatNode node,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        var count = ParameterValueValidator.GetRepeatCount(
            _expressions.Evaluate(node.Count, CreateExpressionContext()));
        RememberParameters(node, new Dictionary<string, object?> { ["count"] = count });

        var stepIntoBody = ConsumeStepForContainer();
        for (var iteration = 0; iteration < count; iteration++)
        {
            // Empty, disabled-only and zero-delay bodies can otherwise complete every await
            // synchronously. A very large repeat would then monopolize the WPF dispatcher and
            // prevent the operator from pressing Stop. Periodically force an actual asynchronous
            // boundary and continue on the thread pool.
            if (iteration > 0 && iteration % CooperativeYieldInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await new ThreadPoolYieldAwaitable();
            }

            if (IsStopRequested())
            {
                return SequenceOutcome.Stop;
            }

            var childPath = new List<int>(iterationPath) { iteration };
            if (stepIntoBody)
            {
                SetPauseBeforeNext();
                stepIntoBody = false;
            }

            var outcome = await ExecuteSequenceAsync(node.Body ?? new List<WorkflowNode>(), childPath, cancellationToken)
                .ConfigureAwait(false);
            if (outcome != SequenceOutcome.Continue)
            {
                return outcome;
            }
        }

        var result = RecordResult(
            node,
            0,
            iterationPath,
            new Dictionary<string, object?> { ["count"] = count });
        RaiseNodeState(node, WorkflowNodeExecutionState.Completed, FormatPath(iterationPath), result);
        MarkStepComplete();
        return SequenceOutcome.Continue;
    }

    private async Task<SequenceOutcome> ExecuteConditionalAsync(
        ConditionalNode node,
        List<int> iterationPath,
        CancellationToken cancellationToken)
    {
        var branches = node.Branches ?? new List<ConditionalBranch>();
        ConditionalBranch? selected = null;
        var selectedIndex = -1;
        for (var index = 0; index < branches.Count; index++)
        {
            var branch = branches[index];
            if (branch.Kind == ConditionalBranchKind.Else)
            {
                selected = branch;
                selectedIndex = index;
                break;
            }

            if (branch.Condition == null)
            {
                throw new WorkflowExecutionException($"Conditional branch {index + 1} has no condition.");
            }

            var condition = ParameterValueValidator.GetBoolean(
                _expressions.Evaluate(branch.Condition, CreateExpressionContext()),
                $"Branch {index + 1} condition");
            if (condition)
            {
                selected = branch;
                selectedIndex = index;
                break;
            }
        }

        RememberParameters(node, EmptyParameters());
        var stepIntoBody = ConsumeStepForContainer();
        if (selected != null)
        {
            if (stepIntoBody)
            {
                SetPauseBeforeNext();
            }

            var outcome = await ExecuteSequenceAsync(
                    selected.Body ?? new List<WorkflowNode>(),
                    iterationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome != SequenceOutcome.Continue)
            {
                return outcome;
            }
        }
        else if (stepIntoBody)
        {
            // Stepping a conditional with no matching branch still consumes one
            // executable node, so pause before the following node.
            SetPauseBeforeNext();
        }

        var values = new Dictionary<string, object?>
        {
            ["branch_index"] = selectedIndex,
            ["branch_kind"] = selected?.Kind.ToString().ToLowerInvariant() ?? "none"
        };
        var result = RecordResult(node, 0, iterationPath, values);
        RaiseNodeState(node, WorkflowNodeExecutionState.Completed, FormatPath(iterationPath), result);
        MarkStepComplete();
        return SequenceOutcome.Continue;
    }

    private IReadOnlyDictionary<string, object?> EvaluateStage(StageNode node)
    {
        var context = CreateExpressionContext();
        var mode = ParameterValueValidator.GetMoveMode(_expressions.Evaluate(node.MoveMode, context));
        var x = ParameterValueValidator.GetFiniteCoordinate(
            _expressions.Evaluate(node.StageX, context),
            "stage_x");
        var y = ParameterValueValidator.GetFiniteCoordinate(
            _expressions.Evaluate(node.StageY, context),
            "stage_y");

        return new Dictionary<string, object?>
        {
            ["move_mode"] = mode == MoveCoordinateMode.Relative ? "relative" : "absolute",
            ["stage_x"] = x,
            ["stage_y"] = y
        };
    }

    private IReadOnlyDictionary<string, object?> EvaluateCamera(CameraNode node)
    {
        var context = CreateExpressionContext();
        var mode = ParameterValueValidator.GetMoveMode(_expressions.Evaluate(node.MoveMode, context));
        var x = ParameterValueValidator.GetFiniteCoordinate(
            _expressions.Evaluate(node.CameraX, context),
            "camera_x");
        var y = ParameterValueValidator.GetFiniteCoordinate(
            _expressions.Evaluate(node.CameraY, context),
            "camera_y");
        return new Dictionary<string, object?>
        {
            ["move_mode"] = mode == MoveCoordinateMode.Relative ? "relative" : "absolute",
            ["camera_x"] = x,
            ["camera_y"] = y
        };
    }

    private IReadOnlyDictionary<string, object?> EvaluateFocus(FocusNode node)
    {
        var context = CreateExpressionContext();
        var hfw = ParameterValueValidator.GetHorizontalFieldWidth(
            _expressions.Evaluate(node.HorizontalFieldWidth, context));
        var range = ParameterValueValidator.GetFocusRange(_expressions.Evaluate(node.Range, context));
        var steps = ParameterValueValidator.GetFocusSteps(_expressions.Evaluate(node.Steps, context));
        return new Dictionary<string, object?>
        {
            ["hfw"] = hfw,
            ["range"] = range,
            ["steps"] = steps
        };
    }

    private IReadOnlyDictionary<string, object?> EvaluateIntegration(IntegrationNode node)
    {
        var context = CreateExpressionContext();
        return new Dictionary<string, object?>
        {
            ["hfw"] = ParameterValueValidator.GetHorizontalFieldWidth(
                _expressions.Evaluate(node.HorizontalFieldWidth, context)),
            ["frame_count"] = ParameterValueValidator.GetIntegrationFrameCount(
                _expressions.Evaluate(node.FrameCount, context)),
            ["image_path"] = ParameterValueValidator.GetAbsoluteImagePath(
                _expressions.Evaluate(node.ImagePath, context))
        };
    }

    private IReadOnlyDictionary<string, object?> EvaluateLive(LiveNode node)
    {
        var context = CreateExpressionContext();
        return new Dictionary<string, object?>
        {
            ["hfw"] = ParameterValueValidator.GetHorizontalFieldWidth(
                _expressions.Evaluate(node.HorizontalFieldWidth, context)),
            ["frame_count"] = ParameterValueValidator.GetLiveFrameCount(
                _expressions.Evaluate(node.FrameCount, context)),
            ["image_path"] = ParameterValueValidator.GetAbsoluteImagePath(
                _expressions.Evaluate(node.ImagePath, context))
        };
    }

    private ExpressionContext CreateExpressionContext()
    {
        var context = new ExpressionContext();
        var document = _document;
        if (document == null)
        {
            return context;
        }

        foreach (var node in document.EnumerateNodesDepthFirst())
        {
            if (!_evaluatedParameters.TryGetValue(node.Id, out var parameters))
            {
                parameters = EvaluateLiteralParameters(node);
            }

            context.SetAction(node, parameters, Results);
        }

        return context;
    }

    private IReadOnlyDictionary<string, object?> EvaluateLiteralParameters(WorkflowNode node)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in node.GetParameterBindings())
        {
            if (pair.Value == null || pair.Value.IsExpression)
            {
                continue;
            }

            try
            {
                values[pair.Key] = _expressions.EvaluateLiteral(pair.Value.RawText).ToObject();
            }
            catch (ExpressionException)
            {
                // The workflow validator reports malformed authored literals. Keeping the field
                // out of the expression context avoids publishing an unvalidated value.
            }
        }

        return values;
    }

    private void RememberParameters(WorkflowNode node, IReadOnlyDictionary<string, object?> parameters)
    {
        _evaluatedParameters[node.Id] = parameters.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private ActionExecutionResult RecordResult(
        WorkflowNode node,
        int correlationId,
        IEnumerable<int> iterationPath,
        Dictionary<string, object?> values)
    {
        var result = new ActionExecutionResult
        {
            ActionId = node.Id,
            ActionKey = node.Key,
            CorrelationId = correlationId,
            IterationPath = iterationPath.ToList(),
            Values = values,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
        Results.Record(result);
        return result;
    }

    private async Task<bool> PauseBeforeNodeAsync(
        WorkflowNode node,
        IReadOnlyList<int> iterationPath,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<DebugResumeMode>? source = null;
        lock (_sync)
        {
            if (_stopRequested)
            {
                return false;
            }

            if (node.HasBreakpoint || _pauseBeforeNext)
            {
                _pauseBeforeNext = false;
                source = new TaskCompletionSource<DebugResumeMode>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pauseSource = source;
            }
        }

        if (source == null)
        {
            return true;
        }

        RaiseNodeState(node, WorkflowNodeExecutionState.Paused, FormatPath(iterationPath));
        ChangeState(WorkflowRunState.Paused, $"Paused before {node.DisplayName}.");
        using (cancellationToken.Register(() => source.TrySetCanceled()))
        {
            var mode = await source.Task.ConfigureAwait(false);

            lock (_sync)
            {
                if (ReferenceEquals(_pauseSource, source))
                {
                    _pauseSource = null;
                }

                if (_stopRequested)
                {
                    return false;
                }

                _stepActive = mode == DebugResumeMode.Step;
            }

            ChangeState(WorkflowRunState.Running, mode == DebugResumeMode.Step ? "Stepping." : "Continuing.");
        }

        return true;
    }

    private void Resume(DebugResumeMode mode)
    {
        TaskCompletionSource<DebugResumeMode>? source;
        lock (_sync)
        {
            if (_state != WorkflowRunState.Paused || _stopRequested)
            {
                return;
            }

            source = _pauseSource;
        }

        source?.TrySetResult(mode);
    }

    private void MarkStepComplete()
    {
        lock (_sync)
        {
            if (_stepActive)
            {
                _stepActive = false;
                _pauseBeforeNext = true;
            }
        }
    }

    private bool ConsumeStepForContainer()
    {
        lock (_sync)
        {
            if (!_stepActive)
            {
                return false;
            }

            _stepActive = false;
            return true;
        }
    }

    private void SetPauseBeforeNext()
    {
        lock (_sync)
        {
            _pauseBeforeNext = true;
        }
    }

    private bool IsStopRequested()
    {
        lock (_sync)
        {
            return _stopRequested;
        }
    }

    private bool IsForceStopRequested()
    {
        lock (_sync)
        {
            return _forceStopRequested;
        }
    }

    private void SetCurrentNode(WorkflowNode? node)
    {
        lock (_sync)
        {
            _currentNode = node;
        }
    }

    private void ChangeState(WorkflowRunState state, string? message = null, Exception? exception = null)
    {
        lock (_stateTransitionSync)
        {
            WorkflowRunState previous;
            lock (_sync)
            {
                previous = _state;
                if (previous == state
                    || (state == WorkflowRunState.Stopping
                        && (!_runActive || IsTerminal(previous))))
                {
                    return;
                }

                _state = state;
            }

            RunStateChanged?.Invoke(
                this,
                new WorkflowRunStateChangedEventArgs(previous, state, CurrentRunId, message, exception));
        }
    }

    private void RaiseNodeState(
        WorkflowNode node,
        WorkflowNodeExecutionState state,
        string iterationPath,
        ActionExecutionResult? result = null,
        Exception? exception = null)
    {
        NodeStateChanged?.Invoke(
            this,
            new WorkflowNodeStateChangedEventArgs(node, state, iterationPath, result, exception));
    }

    private static bool IsTerminal(WorkflowRunState state)
    {
        return state == WorkflowRunState.Idle
               || state == WorkflowRunState.Completed
               || state == WorkflowRunState.Stopped
               || state == WorkflowRunState.Faulted;
    }

    private static string FormatPath(IEnumerable<int> path)
    {
        return string.Join(".", path.Select(value => (value + 1).ToString()));
    }

    private static IReadOnlyDictionary<string, object?> EmptyParameters()
    {
        return new Dictionary<string, object?>();
    }

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Natural completion can win immediately after ForceStop captures the source. The
            // terminal state already represents the desired outcome in that race.
        }
    }

    private enum SequenceOutcome
    {
        Continue,
        Stop,
        Abort
    }

    /// <summary>
    /// Unlike Task.Yield, this awaitable deliberately ignores the caller's synchronization
    /// context. IsCompleted is always false, so it also cannot collapse into a synchronous await
    /// when the thread pool is fast. This keeps Win7 responsive without Task.Delay's coarse timer
    /// granularity adding milliseconds to every cooperative yield.
    /// </summary>
    private readonly struct ThreadPoolYieldAwaitable
    {
        public ThreadPoolYieldAwaiter GetAwaiter() => new ThreadPoolYieldAwaiter();
    }

    private readonly struct ThreadPoolYieldAwaiter : ICriticalNotifyCompletion
    {
        private static readonly WaitCallback InvokeContinuation =
            state => ((Action)state!).Invoke();

        public bool IsCompleted => false;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) => Queue(continuation);

        public void UnsafeOnCompleted(Action continuation) => Queue(continuation);

        private static void Queue(Action continuation)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException(nameof(continuation));
            }

            ThreadPool.QueueUserWorkItem(InvokeContinuation, continuation);
        }
    }
}
