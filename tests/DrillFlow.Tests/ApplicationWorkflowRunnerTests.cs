using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Execution;
using DrillFlow.Application.Http;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Validation;
using DrillFlow.Core.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationWorkflowRunnerTests
{
    [Fact]
    public async Task RunSelectedAsync_PreservesCurrentSessionAndEarlierExpressionResults()
    {
        var transport = new FakeTransport(request => Task.FromResult(
            Response(request, new Dictionary<string, object?>
            {
                ["current_stage_x"] = request.Action == EquipmentActionNames.Stage ? 1.2E-3 : 2.4E-3,
                ["current_stage_y"] = 0d
            })));
        var runner = CreateRunner(transport);
        var stage = new StageNode
        {
            Key = "stage_1"
        };
        var integration = new IntegrationNode
        {
            Key = "integration_1",
            HorizontalFieldWidth = ParameterBinding.Expression("stage_1.result.current_stage_x"),
            ImagePath = ParameterBinding.Literal(@"C:\results\integrated.png")
        };
        var document = Document(stage, integration);

        await runner.RunSelectedAsync(document, stage.Id);
        var sessionId = runner.CurrentRunId;
        var stageResult = Assert.Single(runner.Results.GetAll(stage.Id));

        await runner.RunSelectedAsync(document, integration.Id);

        Assert.Equal(sessionId, runner.CurrentRunId);
        Assert.Same(stageResult, Assert.Single(runner.Results.GetAll(stage.Id)));
        Assert.Single(runner.Results.GetAll(integration.Id));
        Assert.Equal(new[] { "stage", "integration" }, transport.Requests.Select(request => request.Action));
        Assert.Equal(1.2E-3, (double)transport.Requests[1].Parameters["hfw"]!, 12);
    }

    [Fact]
    public async Task RunAsync_AfterSelectedExecutionStartsFreshResultSession()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var first = new StageNode { Key = "first" };

        await runner.RunSelectedAsync(Document(first), first.Id);
        var selectedSessionId = runner.CurrentRunId;
        Assert.Single(runner.Results.GetAll(first.Id));

        var second = new StageNode { Key = "second" };
        await runner.RunAsync(Document(second));

        Assert.NotEqual(selectedSessionId, runner.CurrentRunId);
        Assert.Empty(runner.Results.GetAll(first.Id));
        Assert.Single(runner.Results.GetAll(second.Id));
    }

    [Fact]
    public async Task RunAsync_PublishesEquipmentActionsInOrderAndKeepsDynamicResults()
    {
        var transport = new FakeTransport(request =>
        {
            var properties = new Dictionary<string, object?>
            {
                ["controller_value"] = "preserved"
            };
            if (request.Action == EquipmentActionNames.Stage)
            {
                properties["current_stage_x"] = request.CorrelationId * 0.1d;
                properties["current_stage_y"] = request.CorrelationId * -0.2d;
            }
            else if (request.Action == EquipmentActionNames.Camera)
            {
                properties["current_camera_x"] = 1.2E-3;
                properties["current_camera_y"] = request.CorrelationId * -0.2d;
            }
            else if (request.Action == EquipmentActionNames.Integration)
            {
                properties["image_path"] = @"C:\results\hole.png";
            }

            return Task.FromResult(Response(request, properties));
        });
        var runner = CreateRunner(transport);
        var stage = new StageNode
        {
            Key = "stage_1",
            MoveMode = ParameterBinding.Literal("absolute"),
            StageX = ParameterBinding.Literal("-2.5E-1"),
            StageY = ParameterBinding.Literal("2.5E-1")
        };
        var camera = new CameraNode { Key = "camera_1" };
        var integration = new IntegrationNode
        {
            Key = "integration_1",
            HorizontalFieldWidth = ParameterBinding.Expression("camera_1.result.current_camera_x"),
            ImagePath = ParameterBinding.Literal(@"C:\results\hole.png")
        };
        var document = Document(stage, camera, integration);

        await runner.RunAsync(document);

        Assert.Equal(WorkflowRunState.Completed, runner.State);
        Assert.Equal(new[] { "stage", "camera", "integration" }, transport.Requests.Select(x => x.Action));
        Assert.Equal(new[] { 1, 2, 3 }, transport.Requests.Select(x => x.CorrelationId));
        Assert.Equal(-0.25d, transport.Requests[0].Parameters["stage_x"]);
        Assert.Equal(1.2E-3d, transport.Requests[2].Parameters["hfw"]);
        var integrationResult = runner.Results.GetLatest(integration.Id)!;
        Assert.Equal(@"C:\results\hole.png", integrationResult.Values["image_path"]);
        Assert.Equal("preserved", integrationResult.Values["controller_value"]);
        Assert.Equal("integration", integrationResult.Values["action"]);
        Assert.Equal(0, integrationResult.Values["result"]);
    }

    [Fact]
    public async Task RunAsync_PublishesOmLensAndAutoContrastBrightnessContracts()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var om = new OmNode
        {
            ImagePath = ParameterBinding.Literal(@"C:\results\om.png")
        };
        var lens = new LensNode
        {
            LensMode = ParameterBinding.Literal("lens2")
        };
        var acb = new AutoContrastBrightnessNode
        {
            HorizontalFieldWidth = ParameterBinding.Literal("2.04E-6")
        };

        await runner.RunAsync(Document(om, lens, acb));

        Assert.Equal(
            new[]
            {
                EquipmentActionNames.Om,
                EquipmentActionNames.Lens,
                EquipmentActionNames.AutoContrastBrightness
            },
            transport.Requests.Select(request => request.Action));
        Assert.Equal(@"C:\results\om.png", transport.Requests[0].Parameters["image_path"]);
        Assert.Equal("lens2", transport.Requests[1].Parameters["lens_mode"]);
        Assert.Equal(2.04E-6, (double)transport.Requests[2].Parameters["hfw"]!, 12);
        Assert.Equal("lens2", runner.Results.GetLatest(lens.Id)!.Values["current_lens_mode"]);
    }

    [Fact]
    public async Task Repeat_PreservesEveryIterationResultAndUsesUniqueCorrelations()
    {
        var transport = new FakeTransport(request => Task.FromResult(
            Response(request, new Dictionary<string, object?>
            {
                ["current_stage_x"] = request.CorrelationId * 1E-4,
                ["current_stage_y"] = request.CorrelationId * -2E-4
            })));
        var runner = CreateRunner(transport);
        var stage = new StageNode { Key = "stage_loop" };
        var repeat = new RepeatNode
        {
            Key = "repeat_1",
            Count = ParameterBinding.Literal("3"),
            Body = new List<WorkflowNode> { stage }
        };

        await runner.RunAsync(Document(repeat));

        var results = runner.Results.GetAll(stage.Id);
        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { 1, 2, 3 }, results.Select(x => x.CorrelationId));
        Assert.Equal(new[] { 0, 1, 2 }, results.Select(x => x.IterationPath.Single()));
        Assert.Equal(1E-4, (double)results[0].Values["current_stage_x"]!, 12);
        Assert.Equal(2E-4, (double)results[1].Values["current_stage_x"]!, 12);
        Assert.Equal(3E-4, (double)results[2].Values["current_stage_x"]!, 12);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task RunAsync_RejectsTransportResponseWithDifferentCorrelationId()
    {
        var transport = new FakeTransport(request => Task.FromResult(
            new EquipmentResponseMessage(
                request.CorrelationId + 1,
                request.Action,
                0,
                new Dictionary<string, object?>
                {
                    ["current_stage_x"] = 0d,
                    ["current_stage_y"] = 0d
                })));
        var runner = CreateRunner(transport);
        var stage = new StageNode { Key = "stage_1" };

        var exception = await Assert.ThrowsAsync<WorkflowExecutionException>(() =>
            runner.RunAsync(Document(stage)));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Equal(WorkflowRunState.Faulted, runner.State);
        Assert.Null(runner.Results.GetLatest(stage.Id));
    }

    [Fact]
    public async Task RunAsync_ResultOneFaultsActionStopsWorkflowAndPreservesFailureResult()
    {
        var transport = new FakeTransport(request => Task.FromResult(
            new EquipmentResponseMessage(
                request.CorrelationId,
                request.Action,
                1)));
        var runner = CreateRunner(transport);
        var failed = new StageNode { Key = "stage_failed" };
        var neverStarted = new StageNode { Key = "stage_after_failure" };
        WorkflowNodeStateChangedEventArgs? faultedEvent = null;
        runner.NodeStateChanged += (_, args) =>
        {
            if (args.Node.Id == failed.Id && args.State == WorkflowNodeExecutionState.Faulted)
            {
                faultedEvent = args;
            }
        };

        var exception = await Assert.ThrowsAnyAsync<WorkflowExecutionException>(() =>
            runner.RunAsync(Document(failed, neverStarted)));

        Assert.Contains("result 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkflowRunState.Faulted, runner.State);
        Assert.Single(transport.Requests);
        var result = Assert.Single(runner.Results.GetAll(failed.Id));
        Assert.Equal(1, result.Values["result"]);
        Assert.Equal("stage", result.Values["action"]);
        Assert.False(result.Values.ContainsKey("current_stage_x"));
        Assert.False(result.Values.ContainsKey("current_stage_y"));
        Assert.Same(result, faultedEvent?.Result);
        Assert.Null(runner.Results.GetLatest(neverStarted.Id));
    }

    [Fact]
    public async Task RequestStop_FirstPressStopsImmediatelyWithoutResponseOrAbort()
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
        var first = new StageNode { Key = "stage_1" };
        var second = new StageNode { Key = "stage_2" };

        var runTask = runner.RunAsync(Document(first, second));
        var request = await firstRequestSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        runner.RequestStop();
        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Single(transport.Requests);
        Assert.DoesNotContain(transport.Requests, item => item.Action == EquipmentActionNames.Abort);
        Assert.Null(runner.Results.GetLatest(first.Id));
        Assert.Null(runner.Results.GetLatest(second.Id));

        // Release the deliberately cancellation-unaware fake so the late-task observer can finish.
        responseGate.TrySetResult(Response(request));
    }

    [Fact]
    public async Task RunAsync_RemainsRunningAndDoesNotAdvanceUntilEquipmentResponseArrives()
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
        var move = new StageNode { Key = "stage_waiting" };

        var runTask = runner.RunAsync(Document(move));
        var request = await firstRequestSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.False(runTask.IsCompleted);
        Assert.Equal(WorkflowRunState.Running, runner.State);
        Assert.Equal(move.Id, runner.CurrentNode?.Id);
        Assert.Null(runner.Results.GetLatest(move.Id));

        responseGate.SetResult(Response(request));
        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowRunState.Completed, runner.State);
        Assert.NotNull(runner.Results.GetLatest(move.Id));
    }

    [Fact]
    public async Task RequestStop_CancelsInflightExchangeWithoutRequiringSecondPress()
    {
        var requestSeen = new TaskCompletionSource<EquipmentRequestMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new CancellationAwareTransport(requestSeen, cancellationSeen);
        var runner = CreateRunner(transport);
        var move = new StageNode { Key = "stage_1" };

        var runTask = runner.RunAsync(Document(move));
        await requestSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        runner.RequestStop();
        await cancellationSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Single(transport.Requests);
        Assert.DoesNotContain(transport.Requests, item => item.Action == EquipmentActionNames.Abort);
        Assert.Null(runner.Results.GetLatest(move.Id));
    }

    [Fact]
    public async Task ForceStop_AfterTerminalState_DoesNotRegressStateToStopping()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);

        await runner.RunAsync(Document(new StageNode { Key = "stage_1" }));
        Assert.Equal(WorkflowRunState.Completed, runner.State);

        runner.ForceStop();

        Assert.Equal(WorkflowRunState.Completed, runner.State);
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
        var move = new StageNode { Key = "stage_1", HasBreakpoint = true };

        var runTask = runner.RunAsync(Document(move));
        await paused.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(transport.Requests);
        runner.Continue();
        await runTask;

        Assert.Single(transport.Requests);
        Assert.Equal(WorkflowRunState.Completed, runner.State);
    }

    [Fact]
    public async Task RequestStop_AtBreakpointTransitionsPausedNodeToStopped()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var runner = CreateRunner(transport);
        var move = new StageNode { Key = "stage_paused", HasBreakpoint = true };
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.NodeStateChanged += (_, args) =>
        {
            if (args.Node.Id != move.Id)
            {
                return;
            }

            if (args.State == WorkflowNodeExecutionState.Paused)
            {
                paused.TrySetResult(true);
            }
            else if (args.State == WorkflowNodeExecutionState.Stopped)
            {
                stopped.TrySetResult(true);
            }
        };

        var runTask = runner.RunAsync(Document(move));
        await paused.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        runner.RequestStop();

        await runTask.WithTimeoutAsync(TimeSpan.FromSeconds(1));
        Assert.True(await stopped.Task.WithTimeoutAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Empty(transport.Requests);
        Assert.Null(runner.Results.GetLatest(move.Id));
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
        var move = new StageNode { Key = "stage_1" };

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

    [Fact]
    public async Task HttpActions_RunInsideDesignerAndExposeNestedJsonToLaterExpressions()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var requests = new List<HttpActionRequest>();
        var http = new FakeHttpActionExecutor((request, _) =>
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                return Task.FromResult(new HttpActionResponse(
                    202,
                    "Accepted",
                    new Dictionary<string, string[]> { ["X-Request-Id"] = new[] { "abc" } },
                    "{\"job\":{\"accepted\":true},\"next_url\":\"https://example.test/next\",\"headers\":{\"Authorization\":\"Bearer token\"},\"payload\":{\"ids\":[3,5]}}",
                    "application/json",
                    new Dictionary<string, object?>
                    {
                        ["job"] = new Dictionary<string, object?> { ["accepted"] = true },
                        ["next_url"] = "https://example.test/next",
                        ["headers"] = new Dictionary<string, object?> { ["Authorization"] = "Bearer token" },
                        ["payload"] = new Dictionary<string, object?> { ["ids"] = new object[] { 3d, 5d } }
                    }));
            }

            return Task.FromResult(new HttpActionResponse(
                200,
                "OK",
                new Dictionary<string, string[]>(),
                "{\"done\":true}",
                "application/json",
                new Dictionary<string, object?> { ["done"] = true }));
        });
        var runner = CreateRunner(transport, http);
        var first = new HttpActionNode
        {
            Key = "http_start",
            Url = ParameterBinding.Literal("https://example.test/start")
        };
        var second = new HttpActionNode
        {
            Key = "http_next",
            Method = ParameterBinding.Literal("POST"),
            Url = ParameterBinding.Expression("http_start.result.json.next_url"),
            Headers = ParameterBinding.Expression("http_start.result.json.headers"),
            Body = ParameterBinding.Expression("http_start.result.json.payload"),
            TimeoutMilliseconds = ParameterBinding.Literal("45000")
        };

        await runner.RunAsync(Document(first, second));

        Assert.Empty(transport.Requests);
        Assert.Equal(2, requests.Count);
        Assert.Equal("https://example.test/next", requests[1].Url);
        Assert.Equal("POST", requests[1].Method);
        Assert.Equal(TimeSpan.FromSeconds(45), requests[1].Timeout);
        var secondHeaders = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(requests[1].Headers);
        Assert.Equal("Bearer token", secondHeaders["Authorization"]);
        var firstResult = runner.Results.GetLatest(first.Id)!;
        Assert.Equal(202, firstResult.Values["status_code"]);
        Assert.Equal(true, firstResult.Values["is_success"]);
        Assert.NotNull(firstResult.Values["json"]);
        Assert.Equal(WorkflowRunState.Completed, runner.State);
    }

    [Fact]
    public async Task RequestStop_CancelsCurrentDesignerHttpActionOnceWithoutEquipmentTraffic()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new FakeHttpActionExecutor(async (_, cancellationToken) =>
        {
            started.TrySetResult(true);
            var never = new TaskCompletionSource<HttpActionResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() =>
                   {
                       cancellationSeen.TrySetResult(true);
                       never.TrySetCanceled();
                   }))
            {
                return await never.Task.ConfigureAwait(false);
            }
        });
        var runner = CreateRunner(transport, http);
        var node = new HttpActionNode
        {
            Key = "http_wait",
            Url = ParameterBinding.Literal("https://example.test/wait")
        };
        var stoppedEvents = 0;
        runner.NodeStateChanged += (_, args) =>
        {
            if (args.Node.Id == node.Id && args.State == WorkflowNodeExecutionState.Stopped)
            {
                Interlocked.Increment(ref stoppedEvents);
            }
        };

        var run = runner.RunAsync(Document(node));
        await started.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        runner.RequestStop();

        await cancellationSeen.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        await run.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Equal(1, stoppedEvents);
        Assert.Empty(transport.Requests);
        Assert.Null(runner.Results.GetLatest(node.Id));
    }

    [Fact]
    public async Task RequestStop_NonCooperativeHttpActionStopsPromptlyAndSanitizesLateFailureLog()
    {
        var transport = new FakeTransport(request => Task.FromResult(Response(request)));
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCompletion = new TaskCompletionSource<HttpActionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new FakeHttpActionExecutor((_, _) =>
        {
            started.TrySetResult(true);
            return lateCompletion.Task;
        });
        var logger = new LateHttpWarningLogger();
        var runner = CreateRunner(transport, http, logger);
        var node = new HttpActionNode
        {
            Key = "http_non_cooperative",
            Url = ParameterBinding.Literal(
                "https://operator:password@example.test/camera/frame?access_token=secret#preview")
        };

        var run = runner.RunAsync(Document(node));
        await started.Task.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        runner.RequestStop();

        await run.WithTimeoutAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkflowRunState.Stopped, runner.State);
        Assert.Empty(transport.Requests);
        Assert.Null(runner.Results.GetLatest(node.Id));

        lateCompletion.TrySetException(
            new InvalidOperationException("late HTTP failure body_token=exception-secret"));
        var warning = await logger.WarningLogged.Task.WithTimeoutAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("https://example.test/camera/frame", warning, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("password", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("body_token", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("preview", warning, StringComparison.Ordinal);
        Assert.Null(logger.WarningException);
    }

    private static WorkflowRunner CreateRunner(
        IEquipmentFileTransport transport,
        IHttpActionExecutor? httpActions = null,
        ILogger<WorkflowRunner>? logger = null)
    {
        return new WorkflowRunner(
            transport,
            httpActions ?? new FakeHttpActionExecutor((_, _) =>
                throw new InvalidOperationException("Unexpected HTTP action.")),
            new IncrementingCorrelationProvider(),
            new ExpressionEngine(),
            new WorkflowValidator(),
            new RunResultStore(),
            logger ?? NullLogger<WorkflowRunner>.Instance);
    }

    private sealed class FakeHttpActionExecutor : IHttpActionExecutor
    {
        private readonly Func<HttpActionRequest, CancellationToken, Task<HttpActionResponse>> _execute;

        public FakeHttpActionExecutor(
            Func<HttpActionRequest, CancellationToken, Task<HttpActionResponse>> execute)
        {
            _execute = execute;
        }

        public Task<HttpActionResponse> ExecuteAsync(
            HttpActionRequest request,
            CancellationToken cancellationToken = default)
        {
            return _execute(request, cancellationToken);
        }
    }

    private sealed class LateHttpWarningLogger : ILogger<WorkflowRunner>
    {
        public TaskCompletionSource<string> WarningLogged { get; } = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? WarningException { get; private set; }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningException = exception;
                WarningLogged.TrySetResult(formatter(state, exception));
            }
        }

        private sealed class EmptyScope : IDisposable
        {
            public static EmptyScope Instance { get; } = new EmptyScope();

            public void Dispose()
            {
            }
        }
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
        var responseProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (request.Action)
        {
            case EquipmentActionNames.Stage:
                responseProperties["current_stage_x"] = 0d;
                responseProperties["current_stage_y"] = 0d;
                break;
            case EquipmentActionNames.Camera:
                responseProperties["current_camera_x"] = 0d;
                responseProperties["current_camera_y"] = 0d;
                break;
            case EquipmentActionNames.Focus:
                responseProperties["z_to_sharpness_2d"] = null;
                break;
            case EquipmentActionNames.Integration:
            case EquipmentActionNames.Live:
                responseProperties["hfw"] = request.Parameters["hfw"];
                responseProperties["frame_count"] = request.Parameters["frame_count"];
                responseProperties["image_path"] = request.Parameters["image_path"];
                break;
            case EquipmentActionNames.Om:
                responseProperties["image_path"] = request.Parameters["image_path"];
                break;
            case EquipmentActionNames.Lens:
                var requestedMode = (string)request.Parameters["lens_mode"]!;
                responseProperties["current_lens_mode"] = requestedMode == "lens2"
                    ? "lens2"
                    : "lens1";
                break;
        }

        if (properties != null)
        {
            foreach (var property in properties)
            {
                responseProperties[property.Key] = property.Value;
            }
        }

        return new EquipmentResponseMessage(
            request.CorrelationId,
            request.Action,
            0,
            responseProperties);
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
