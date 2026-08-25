using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Execution;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Validation;
using DrillFlow.Core.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationWorkflowRunnerTests
{
    [Fact]
    public async Task RunAsync_PublishesEquipmentActionsInOrderAndKeepsDynamicResults()
    {
        var transport = new FakeTransport(request => Task.FromResult(
            Response(request, new Dictionary<string, object?>
            {
                ["measured_distance"] = request.Command == "measure" ? 1.2E-3 : null,
                ["drill_result_path"] = request.Command == "drill" ? @"C:\results\hole.csv" : null
            })));
        var runner = CreateRunner(transport);
        var move = new MoveNode
        {
            Key = "move_1",
            MoveMode = ParameterBinding.Literal("absolute"),
            MoveX = ParameterBinding.Literal("-2.5E-1"),
            MoveY = ParameterBinding.Literal("2.5E-1")
        };
        var measure = new MeasureNode { Key = "measure_1", Thickness = ParameterBinding.Literal("1E-3") };
        var drill = new DrillNode
        {
            Key = "drill_1",
            Thickness = ParameterBinding.Expression("measure_1.result.measured_distance"),
            DrillResultPath = ParameterBinding.Literal(@"C:\results\hole.csv")
        };
        var document = Document(move, measure, drill);

        await runner.RunAsync(document);

        Assert.Equal(WorkflowRunState.Completed, runner.State);
        Assert.Equal(new[] { "move", "measure", "drill" }, transport.Requests.Select(x => x.Command));
        Assert.Equal(new[] { 1, 2, 3 }, transport.Requests.Select(x => x.Index));
        Assert.Equal(-0.25d, transport.Requests[0].Parameters["move_x"]);
        Assert.Equal(1.2E-3d, transport.Requests[2].Parameters["thickness"]);
        Assert.Equal(@"C:\results\hole.csv", runner.Results.GetLatest(drill.Id)!.Values["drill_result_path"]);
    }

    [Fact]
    public async Task Repeat_PreservesEveryIterationResultAndUsesUniqueCorrelations()
    {
        var transport = new FakeTransport(request => Task.FromResult(
            Response(request, new Dictionary<string, object?>
            {
                ["measured_distance"] = request.Index * 1E-4
            })));
        var runner = CreateRunner(transport);
        var measure = new MeasureNode { Key = "measure_loop", Thickness = ParameterBinding.Literal("1E-3") };
        var repeat = new RepeatNode
        {
            Key = "repeat_1",
            Count = ParameterBinding.Literal("3"),
            Body = new List<WorkflowNode> { measure }
        };

        await runner.RunAsync(Document(repeat));

        var results = runner.Results.GetAll(measure.Id);
        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { 1, 2, 3 }, results.Select(x => x.CorrelationId));
        Assert.Equal(new[] { 0, 1, 2 }, results.Select(x => x.IterationPath.Single()));
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task RequestStop_DoesNotPublishAbortAndStopsAfterInflightResponse()
    {
        var responseGate = new TaskCompletionSource<EquipmentResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestSeen = new TaskCompletionSource<EquipmentRequestMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(request =>
        {
            firstRequestSeen.TrySetResult(request);
            return responseGate.Task;
        });
        var runner = CreateRunner(transport);
        var first = new MoveNode { Key = "move_1" };
        var second = new MoveNode { Key = "move_2" };

        var runTask = runner.RunAsync(Document(first, second));
        var request = await firstRequestSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        runner.RequestStop();
        Assert.Equal(WorkflowRunState.Stopping, runner.State);
        responseGate.SetResult(Response(request));
        await runTask;

        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Single(transport.Requests);
        Assert.DoesNotContain(transport.Requests, item => item.Command == "abort");
        Assert.NotNull(runner.Results.GetLatest(first.Id));
        Assert.Null(runner.Results.GetLatest(second.Id));
    }

    [Fact]
    public async Task ForceStop_AfterGracefulStop_CancelsInflightExchangeWithoutPublishingAbort()
    {
        var requestSeen = new TaskCompletionSource<EquipmentRequestMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new CancellationAwareTransport(requestSeen, cancellationSeen);
        var runner = CreateRunner(transport);
        var move = new MoveNode { Key = "move_1" };

        var runTask = runner.RunAsync(Document(move));
        await requestSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        runner.RequestStop();
        Assert.Equal(WorkflowRunState.Stopping, runner.State);
        Assert.False(cancellationSeen.Task.IsCompleted);

        runner.ForceStop();
        await cancellationSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Single(transport.Requests);
        Assert.DoesNotContain(transport.Requests, item => item.Command == "abort");
        Assert.Null(runner.Results.GetLatest(move.Id));
    }

    [Fact]
    public async Task Breakpoint_PausesBeforePublishingUntilContinue()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunStateChanged += (_, args) =>
        {
            if (args.State == WorkflowRunState.Paused)
            {
                paused.TrySetResult(true);
            }
        };
        var move = new MoveNode { Key = "move_1", HasBreakpoint = true };

        var runTask = runner.RunAsync(Document(move));
        await paused.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(transport.Requests);
        runner.Continue();
        await runTask;

        Assert.Single(transport.Requests);
        Assert.Equal(WorkflowRunState.Completed, runner.State);
    }

    [Fact]
    public async Task RequestStop_CancelsLocalDelayPromptly()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var delay = new DelayNode
        {
            Key = "delay_1",
            DurationMilliseconds = ParameterBinding.Literal("29999")
        };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.NodeStateChanged += (_, args) =>
        {
            if (args.Node.Id != delay.Id)
            {
                return;
            }

            if (args.State == WorkflowNodeExecutionState.Running)
            {
                started.TrySetResult(true);
            }
            else if (args.State == WorkflowNodeExecutionState.Stopped)
            {
                stopped.TrySetResult(true);
            }
        };

        var runTask = runner.RunAsync(Document(delay));
        await started.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        runner.RequestStop();

        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.True(await stopped.Task.WithTimeoutAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Null(runner.Results.GetLatest(delay.Id));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("disabled")]
    [InlineData("zero-delay")]
    public async Task Repeat_LargeLocalOnlyBodyYieldsSoOperatorStopRemainsResponsive(string bodyKind)
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var repeat = new RepeatNode
        {
            Key = "repeat_1",
            Count = ParameterBinding.Literal(int.MaxValue.ToString())
        };

        if (bodyKind == "disabled")
        {
            repeat.Body.Add(new DelayNode
            {
                Key = "disabled_delay",
                IsEnabled = false,
                DurationMilliseconds = ParameterBinding.Literal("0")
            });
        }
        else if (bodyKind == "zero-delay")
        {
            repeat.Body.Add(new DelayNode
            {
                Key = "zero_delay",
                DurationMilliseconds = ParameterBinding.Literal("0")
            });
        }

        // Capture the Task returned by RunAsync on a separate caller thread. Without a forced
        // asynchronous boundary, the call itself never returns for these local-only repeats, just
        // as it would monopolize the WPF dispatcher before the Stop button could be processed.
        var invocationReturned = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Task.Run(() =>
        {
            try
            {
                invocationReturned.TrySetResult(runner.RunAsync(Document(repeat)));
            }
            catch (Exception exception)
            {
                invocationReturned.TrySetException(exception);
            }
        });

        Task runTask;
        try
        {
            runTask = await invocationReturned.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            // Also releases the worker if this regression ever reappears, so a failed test cannot
            // leave an Int32.MaxValue loop running in the test host.
            runner.RequestStop();
        }

        await invocation.WithTimeoutAsync(TimeSpan.FromSeconds(1));
        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Step_ConditionalWithoutMatchingBranch_PausesBeforeNextNode()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var firstPause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseCount = 0;
        runner.RunStateChanged += (_, args) =>
        {
            if (args.State != WorkflowRunState.Paused)
            {
                return;
            }

            if (Interlocked.Increment(ref pauseCount) == 1)
            {
                firstPause.TrySetResult(true);
            }
            else
            {
                secondPause.TrySetResult(true);
            }
        };

        var conditional = new ConditionalNode { Key = "choice", HasBreakpoint = true };
        conditional.Branches[0].Condition = ParameterBinding.Literal("false");
        var move = new MoveNode { Key = "move_1" };

        var runTask = runner.RunAsync(Document(conditional, move));
        await firstPause.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        runner.Step();
        await secondPause.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(transport.Requests);
        Assert.NotNull(runner.Results.GetLatest(conditional.Id));
        runner.Continue();
        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Single(transport.Requests);
        Assert.Equal(WorkflowRunState.Completed, runner.State);
    }

    private static WorkflowRunner CreateRunner(IEquipmentFileTransport transport)
    {
        return new WorkflowRunner(
            transport,
            new IncrementingCorrelationProvider(),
            new ExpressionEngine(),
            new WorkflowValidator(),
            new RunResultStore(),
            NullLogger<WorkflowRunner>.Instance);
    }

    private static WorkflowDocument Document(params WorkflowNode[] nodes)
    {
        return new WorkflowDocument
        {
            Name = "Test workflow",
            Nodes = nodes.ToList()
        };
    }

    private static EquipmentResponseMessage Response(
        EquipmentRequestMessage request,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        return new EquipmentResponseMessage(request.Index, "return", properties);
    }

    private sealed class IncrementingCorrelationProvider : ICorrelationIdProvider
    {
        private int _value;

        public Task<int> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref _value));
        }
    }

    private sealed class FakeTransport : IEquipmentFileTransport
    {
        private readonly Func<EquipmentRequestMessage, Task<EquipmentResponseMessage>> _exchange;

        public FakeTransport(Func<EquipmentRequestMessage, Task<EquipmentResponseMessage>> exchange)
        {
            _exchange = exchange;
        }

        public List<EquipmentRequestMessage> Requests { get; } = new List<EquipmentRequestMessage>();

        public Task<EquipmentResponseMessage> ExchangeAsync(
            EquipmentRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _exchange(request);
        }
    }

    private sealed class CancellationAwareTransport : IEquipmentFileTransport
    {
        private readonly TaskCompletionSource<EquipmentRequestMessage> _requestSeen;
        private readonly TaskCompletionSource<bool> _cancellationSeen;

        public CancellationAwareTransport(
            TaskCompletionSource<EquipmentRequestMessage> requestSeen,
            TaskCompletionSource<bool> cancellationSeen)
        {
            _requestSeen = requestSeen;
            _cancellationSeen = cancellationSeen;
        }

        public List<EquipmentRequestMessage> Requests { get; } = new List<EquipmentRequestMessage>();

        public async Task<EquipmentResponseMessage> ExchangeAsync(
            EquipmentRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            _requestSeen.TrySetResult(request);
            var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() =>
                   {
                       _cancellationSeen.TrySetResult(true);
                       never.TrySetCanceled();
                   }))
            {
                await never.Task.ConfigureAwait(false);
            }

            throw new InvalidOperationException("The cancellation-only exchange unexpectedly completed.");
        }
    }
}
