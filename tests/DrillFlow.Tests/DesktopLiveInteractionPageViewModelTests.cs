using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Execution;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopLiveInteractionPageViewModelTests
{
    [Fact]
    public async Task HfwZoom_CancelsOldFrameAndRestartsAtHalfWidth()
    {
        var session = new PendingFrameSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1E-3, Assert.Single(session.FrameWidths));
        Assert.True(viewModel.ZoomFrameInCommand.CanExecute(null));

        viewModel.ZoomFrameInCommand.Execute(null);

        await WaitUntilAsync(() => session.FrameWidths.Count >= 2);
        Assert.Equal(0.5E-3, viewModel.HorizontalFieldWidthMetres);
        Assert.Equal("0.5", viewModel.HorizontalFieldWidthText);
        Assert.Equal(new[] { 1E-3, 0.5E-3 }, session.FrameWidths);

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task HfwEditor_EnforcesStrictEquipmentUpperBound()
    {
        var viewModel = CreateViewModel(
            new PendingFrameSession(),
            new BlockingResponseSimulator());

        viewModel.HorizontalFieldWidthUnit = "m";
        viewModel.HorizontalFieldWidthText = "2.4E-3";

        Assert.Equal(1E-3, viewModel.HorizontalFieldWidthMetres);
        Assert.NotEmpty(viewModel.HorizontalFieldWidthValidationMessage);
        Assert.False(viewModel.ZoomFrameInCommand.CanExecute(null));

        viewModel.Activate();
        viewModel.HorizontalFieldWidthText = "2.39E-3";
        Assert.Empty(viewModel.HorizontalFieldWidthValidationMessage);
        Assert.Equal(2.39E-3, viewModel.HorizontalFieldWidthMetres);
        Assert.False(viewModel.ZoomFrameOutCommand.CanExecute(null));

        viewModel.Deactivate();
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task HfwChange_ScalesPixelPitchAndLocksMoveUntilMatchingFrameArrives()
    {
        var session = new PendingFrameSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        SetLoadedImage(viewModel);
        viewModel.PixelPitchUnit = "um";
        viewModel.PixelPitchText = "2";
        var target = new LiveImageTarget(20, 20, 100, 100, 1E-3, 1E-3);
        viewModel.Activate();
        await session.FrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.MoveToTargetCommand.CanExecute(target));

        viewModel.HorizontalFieldWidthText = "0.5";

        Assert.Equal(0.5E-3, viewModel.HorizontalFieldWidthMetres);
        Assert.Equal(1E-6, viewModel.PixelPitchMetres);
        Assert.Equal("1", viewModel.PixelPitchText);
        Assert.True(viewModel.IsFrameCalibrationPending);
        Assert.False(viewModel.IsDisplayedFrameCalibrationCurrent);
        Assert.False(viewModel.MoveToTargetCommand.CanExecute(target));
        await WaitUntilAsync(() => session.FrameWidths.Count >= 2);

        ApplyDecodedFrame(viewModel, 0.5E-3);

        Assert.True(viewModel.IsDisplayedFrameCalibrationCurrent);
        Assert.False(viewModel.IsFrameCalibrationPending);
        Assert.True(viewModel.MoveToTargetCommand.CanExecute(target));

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task PixelPitchLink_IsEnabledByDefaultAndCanKeepCalibrationFixed()
    {
        var viewModel = CreateViewModel(
            new PendingFrameSession(),
            new BlockingResponseSimulator());
        viewModel.PixelPitchUnit = "um";
        viewModel.PixelPitchText = "2";

        Assert.True(viewModel.IsPixelPitchLinkedToHorizontalFieldWidth);
        viewModel.IsPixelPitchLinkedToHorizontalFieldWidth = false;
        viewModel.HorizontalFieldWidthText = "0.5";

        Assert.Equal(2E-6, viewModel.PixelPitchMetres, 12);
        Assert.Equal("2", viewModel.PixelPitchText);

        viewModel.IsPixelPitchLinkedToHorizontalFieldWidth = true;
        viewModel.HorizontalFieldWidthText = "0.25";

        Assert.Equal(1E-6, viewModel.PixelPitchMetres, 12);
        Assert.Equal("1", viewModel.PixelPitchText);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task ImageTargetWithoutPixelPitch_ShowsVisibleCalibrationWarning()
    {
        var viewModel = CreateViewModel(
            new PendingFrameSession(),
            new BlockingResponseSimulator());
        ApplyDecodedFrame(viewModel, 1E-3);

        var created = viewModel.TryCreateMoveTarget(
            100d,
            100d,
            100d,
            100d,
            50d,
            50d,
            out var target);

        Assert.False(created);
        Assert.Null(target);
        Assert.True(viewModel.StatusIsWarning);
        Assert.Equal("LiveStatusPixelPitchRequired", viewModel.StatusMessage);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task ManualActionEditors_ExposeCanonicalDefaultsAndImmediateValidation()
    {
        var session = new PendingFrameSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("relative", viewModel.StageMoveMode);
        Assert.Equal("0E0", viewModel.StageInputXText);
        Assert.Equal("relative", viewModel.CameraMoveMode);
        Assert.Equal("50E-6", viewModel.FocusRangeText);
        Assert.Equal("13", viewModel.FocusStepsText);
        Assert.Equal(8, viewModel.IntegrationFrameCount);
        Assert.True(viewModel.ExecuteStageMoveCommand.CanExecute(null));
        Assert.True(viewModel.ExecuteCameraMoveCommand.CanExecute(null));
        Assert.True(viewModel.ExecuteFocusCommand.CanExecute(null));

        viewModel.StageInputXText = "NaN";
        viewModel.CameraMoveMode = "RELATIVE";
        viewModel.FocusStepsText = "3";
        viewModel.IntegrationFrameCount = 3;

        Assert.NotEmpty(viewModel.StageMoveValidationMessage);
        Assert.NotEmpty(viewModel.CameraMoveValidationMessage);
        Assert.NotEmpty(viewModel.FocusValidationMessage);
        Assert.False(viewModel.ExecuteStageMoveCommand.CanExecute(null));
        Assert.False(viewModel.ExecuteCameraMoveCommand.CanExecute(null));
        Assert.False(viewModel.ExecuteFocusCommand.CanExecute(null));
        Assert.False(viewModel.CaptureCommand.CanExecute(null));

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task CameraResponse_UpdatesLatestAbsoluteCameraPosition()
    {
        var session = new InteractiveMoveSession
        {
            CameraResponseXMetres = -4.25E-6,
            CameraResponseYMetres = 8.75E-6
        };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        await viewModel.ExecuteCameraMoveCommand.ExecuteAsync(null);
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.HasCameraPosition);
        Assert.Contains("-4.25", viewModel.CameraXText);
        Assert.Contains("8.75", viewModel.CameraYText);
        Assert.Equal(702, viewModel.LastCorrelationId);

        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task FocusResponse_PreservesEverySampleForAutoScaledScatterChart()
    {
        var session = new InteractiveMoveSession
        {
            FocusResponseSamples = new IReadOnlyList<double>[]
            {
                new[] { 0.25E-6, 450d },
                new[] { 1.5E-6, 900d },
                new[] { 3.75E-6, 1800d }
            }
        };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        await viewModel.ExecuteFocusCommand.ExecuteAsync(null);
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.HasFocusSamples);
        Assert.Equal(3, viewModel.FocusSamples.Count);
        Assert.Equal(0.25E-6, viewModel.FocusSamples[0].ZMetres, 15);
        Assert.Equal(450d, viewModel.FocusSamples[0].Sharpness, 15);
        Assert.Equal(3.75E-6, viewModel.FocusSamples[2].ZMetres, 15);
        Assert.Equal(1800d, viewModel.FocusSamples[2].Sharpness, 15);
        Assert.Equal(703, viewModel.LastCorrelationId);

        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task OwnedImageCleanup_DeletesOnlyExactRequestedResponsePath()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DrillFlow.LiveOwnedImageTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ownedPath = System.IO.Path.Combine(directory, "live-1.bmp");
            var controllerPath = System.IO.Path.Combine(directory, "controller.bmp");
            File.WriteAllBytes(ownedPath, new byte[] { 1 });
            File.WriteAllBytes(controllerPath, new byte[] { 2 });
            var viewModel = CreateViewModel(
                new PendingFrameSession(),
                new BlockingResponseSimulator());
            var cleanup = typeof(LiveInteractionPageViewModel).GetMethod(
                "TryDeleteOwnedResponseImage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cleanup);

            cleanup!.Invoke(
                viewModel,
                new object[]
                {
                    ImageExchange(1, ownedPath, ownedPath)
                });
            cleanup.Invoke(
                viewModel,
                new object[]
                {
                    ImageExchange(2, System.IO.Path.Combine(directory, "live-2.bmp"), controllerPath)
                });

            await WaitUntilAsync(() => !File.Exists(ownedPath));
            Assert.True(File.Exists(controllerPath));
            await viewModel.ShutdownAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task Stop_FirstPressCancelsPendingFrameAndBecomesTerminalWithoutResponseTimeout()
    {
        var session = new PendingFrameSession();
        var simulator = new BlockingResponseSimulator();
        var viewModel = CreateViewModel(session, simulator);
        viewModel.Activate();
        await session.FrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsInteractionActive);

        var stopwatch = Stopwatch.StartNew();
        viewModel.StopCommand.Execute(null);
        await session.FrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);

        Assert.False(viewModel.IsStreamingRequested);
        Assert.False(viewModel.IsStreaming);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task FrameEquipmentFailure_StopsWithoutTransientRetryAndKeepsErrorStatus()
    {
        var session = new EquipmentFailureFrameSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());

        viewModel.Activate();

        await WaitUntilAsync(() => viewModel.StatusIsError && !viewModel.IsInteractionActive);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Equal(1, session.FrameCallCount);
        Assert.False(viewModel.StatusIsWarning);
        Assert.Equal("LiveStatusFrameFailed", viewModel.StatusMessage);
        Assert.True(viewModel.StartCommand.CanExecute(null));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task Deactivate_CancelsContinuousResponderAndPendingFrame()
    {
        var session = new PendingFrameSession();
        var simulator = new BlockingResponseSimulator { BlockActiveRequestRead = true };
        var viewModel = CreateViewModel(session, simulator);
        viewModel.Activate();
        await session.FrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        viewModel.IsContinuousResponseGenerationEnabled = true;
        await simulator.ActiveRequestReadStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        viewModel.Deactivate();

        await simulator.ActiveRequestReadCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await session.FrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);
        Assert.False(viewModel.IsContinuousResponseGenerationEnabled);
        Assert.Equal(1, simulator.ActiveRequestReadCount);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task Move_PreemptsFrameRunsExclusivelyAndResumesFrameAfterResponse()
    {
        var session = new InteractiveMoveSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        SetLoadedImage(viewModel);
        viewModel.PixelPitchText = "1E-6";
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        var target = new LiveImageTarget(25, 30, 100, 100, 1E-3, -2E-3);

        await viewModel.MoveToTargetCommand.ExecuteAsync(target);
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(session.FirstFrameCanceled.Task.IsCompleted);
        Assert.True(session.MoveStartedAfterFrameCancellation);
        Assert.Equal(1, session.MaximumConcurrentEquipmentCalls);
        Assert.Equal(1, session.MoveCallCount);
        Assert.True(viewModel.IsStreamingRequested);
        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task CancelNonLiveStageRequest_CancelsMoveAndAutomaticallyResumesLiveFrames()
    {
        var session = new InteractiveMoveSession { BlockMove = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.CancelNonLiveRequestCommand.CanExecute(null));
        var move = viewModel.ExecuteStageMoveCommand.ExecuteAsync(null);
        await session.MoveStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsNonLiveRequestPending);
        Assert.True(viewModel.CancelNonLiveRequestCommand.CanExecute(null));
        viewModel.CancelNonLiveRequestCommand.Execute(null);

        Assert.False(viewModel.IsNonLiveRequestPending);
        Assert.False(viewModel.CancelNonLiveRequestCommand.CanExecute(null));
        await session.MoveCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await move.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, session.MaximumConcurrentEquipmentCalls);
        Assert.True(viewModel.IsStreamingRequested);

        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task ImageTargetMove_UsesExactRenderedPointAndCancelClearsOnlyItsMarker()
    {
        var session = new InteractiveMoveSession { BlockMove = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        ApplyDecodedFrame(viewModel, 1E-3);
        viewModel.PixelPitchUnit = "um";
        viewModel.PixelPitchText = "1";
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        // A 100x100 source with a 50x100 natural DIP preview renders at x=50..150 in this
        // 200x200 viewport. The click is therefore exactly source pixel (75, 25).
        Assert.True(viewModel.TryCreateMoveTarget(
            50d,
            100d,
            200d,
            200d,
            125d,
            50d,
            out var target));
        Assert.NotNull(target);
        Assert.Equal(75d, target!.PixelX, 12);
        Assert.Equal(25d, target.PixelY, 12);

        var move = viewModel.MoveToTargetCommand.ExecuteAsync(target);
        await session.MoveStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsTargetMarkerVisible);
        Assert.Equal(25E-6, session.LastStageMoveXMetres, 12);
        Assert.Equal(-25E-6, session.LastStageMoveYMetres, 12);
        viewModel.CancelNonLiveRequestCommand.Execute(null);

        Assert.False(viewModel.IsTargetMarkerVisible);
        await session.MoveCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await move.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.HasTarget);
        Assert.Equal(75d, viewModel.TargetPixelX, 12);
        Assert.Equal(25d, viewModel.TargetPixelY, 12);

        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task Deactivate_CancelsOwnedMoveAndDoesNotResumeFrameLoop()
    {
        var session = new InteractiveMoveSession { BlockMove = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        SetLoadedImage(viewModel);
        viewModel.PixelPitchText = "1E-6";
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        var target = new LiveImageTarget(20, 20, 100, 100, 1E-3, 1E-3);
        var move = viewModel.MoveToTargetCommand.ExecuteAsync(target);
        await session.MoveStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        viewModel.Deactivate();

        await session.MoveCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await move.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, session.FrameCallCount);
        Assert.False(viewModel.IsInteractionActive);
        Assert.False(viewModel.IsStreamingRequested);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_CancelsPendingNonLiveRequestWithoutRestartingLiveFrames()
    {
        var session = new InteractiveMoveSession { BlockMove = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        var move = viewModel.ExecuteStageMoveCommand.ExecuteAsync(null);
        await session.MoveStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        await viewModel.ShutdownAsync().WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await move.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(session.MoveCanceled.Task.IsCompleted);
        Assert.Equal(1, session.FrameCallCount);
        Assert.False(viewModel.IsStreamingRequested);
        Assert.False(viewModel.IsNonLiveRequestPending);
        Assert.False(viewModel.CancelNonLiveRequestCommand.CanExecute(null));
    }

    [Fact]
    public async Task SuccessfulMove_FromStoppedPreviewAlwaysResumesFrameLoop()
    {
        var session = new InteractiveMoveSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        SetLoadedImage(viewModel);
        viewModel.PixelPitchText = "1E-6";
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        viewModel.StopCommand.Execute(null);
        await session.FirstFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);

        await viewModel.MoveToTargetCommand.ExecuteAsync(
            new LiveImageTarget(40, 40, 100, 100, -1E-3, 2E-3));
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsStreamingRequested);
        Assert.False(viewModel.IsTargetMarkerVisible);
        Assert.True(viewModel.HasTarget);
        Assert.Equal(1, session.MoveCallCount);
        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task FailedMove_LeavesFrameStoppedForOperatorDecision()
    {
        var session = new InteractiveMoveSession { FailMove = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        SetLoadedImage(viewModel);
        viewModel.PixelPitchText = "1E-6";
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        await viewModel.MoveToTargetCommand.ExecuteAsync(
            new LiveImageTarget(40, 40, 100, 100, 1E-3, 2E-3));
        await Task.Delay(100);

        Assert.Equal(1, session.FrameCallCount);
        Assert.False(viewModel.IsStreamingRequested);
        Assert.False(viewModel.IsInteractionActive);
        Assert.True(viewModel.StatusIsError);
        Assert.False(viewModel.IsTargetMarkerVisible);
        Assert.True(viewModel.HasTarget);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task Capture_PreemptsFrameAndDeactivateCancelsOwnedCaptureWithoutOverlap()
    {
        var session = new InteractiveMoveSession { BlockCapture = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        var capture = viewModel.CaptureCommand.ExecuteAsync(null);
        await session.CaptureStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(session.CaptureStartedAfterFrameCancellation);
        Assert.Equal(1, session.MaximumConcurrentEquipmentCalls);
        viewModel.Deactivate();
        await session.CaptureCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await capture.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, session.FrameCallCount);
        Assert.False(viewModel.IsInteractionActive);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task CancelNonLiveIntegrationRequest_CancelsCaptureAndAutomaticallyResumesLiveFrames()
    {
        var session = new InteractiveMoveSession { BlockCapture = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        var capture = viewModel.CaptureCommand.ExecuteAsync(null);
        await session.CaptureStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsNonLiveRequestPending);
        Assert.True(viewModel.CancelNonLiveRequestCommand.CanExecute(null));
        viewModel.CancelNonLiveRequestCommand.Execute(null);

        Assert.False(viewModel.CancelNonLiveRequestCommand.CanExecute(null));
        await session.CaptureCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await capture.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, session.MaximumConcurrentEquipmentCalls);
        Assert.True(viewModel.IsStreamingRequested);

        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task FailedIntegration_LeavesStreamStoppedAndPreservesErrorStatus()
    {
        var session = new InteractiveMoveSession { FailCapture = true };
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        await viewModel.CaptureCommand.ExecuteAsync(null);
        await Task.Delay(100);

        Assert.True(session.FirstFrameCanceled.Task.IsCompleted);
        Assert.Equal(1, session.FrameCallCount);
        Assert.False(viewModel.IsStreamingRequested);
        Assert.False(viewModel.IsInteractionActive);
        Assert.True(viewModel.StatusIsError);
        Assert.Equal("LiveStatusCaptureFailed", viewModel.StatusMessage);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task SuccessfulIntegration_WithCanceledSaveResumesPreviousStreamIntent()
    {
        var session = new InteractiveMoveSession();
        var viewModel = CreateViewModel(session, new BlockingResponseSimulator());
        viewModel.Activate();
        await session.FirstFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        await viewModel.CaptureCommand.ExecuteAsync(null);
        await session.SecondFrameStarted.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));

        Assert.True(session.FirstFrameCanceled.Task.IsCompleted);
        Assert.Equal(2, session.FrameCallCount);
        Assert.True(viewModel.IsStreamingRequested);
        viewModel.StopCommand.Execute(null);
        await session.SecondFrameCanceled.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        await viewModel.ShutdownAsync();
    }

    private static LiveInteractionPageViewModel CreateViewModel(
        ILiveInteractionSession session,
        IEquipmentResponseSimulator simulator)
    {
        return new LiveInteractionPageViewModel(
            session,
            new StubFileDialogService(),
            new StubCaptureSnapshotStore(),
            new StubImageDecoder(),
            new StubDefaultFileLauncher(),
            simulator,
            new StubTemporaryResponseImageService(),
            new StubExchangeFolderLauncher(),
            Options.Create(new EquipmentCommunicationOptions
            {
                ExchangeDirectory = System.IO.Path.GetTempPath(),
                RequestFileName = "request.xml",
                ResponseFileName = "response.xml",
                ResponseTimeout = TimeSpan.FromSeconds(30),
                PollingInterval = TimeSpan.FromMilliseconds(10)
            }),
            new StubLocalizationService(),
            new StubWorkflowExecutionFacade(),
            NullLogger<LiveInteractionPageViewModel>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void SetLoadedImage(LiveInteractionPageViewModel viewModel)
    {
        var field = typeof(LiveInteractionPageViewModel).GetField(
            "_liveImageSource",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(viewModel, new DrawingImage());
        var calibrationField = typeof(LiveInteractionPageViewModel).GetField(
            "_isDisplayedFrameCalibrationCurrent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(calibrationField);
        calibrationField!.SetValue(viewModel, true);
        Assert.True(viewModel.HasImage);
    }

    private static void ApplyDecodedFrame(
        LiveInteractionPageViewModel viewModel,
        double horizontalFieldWidthMetres)
    {
        var method = typeof(LiveInteractionPageViewModel).GetMethod(
            "ApplyImageResponse",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(
            viewModel,
            new object?[]
            {
                new EquipmentResponseMessage(
                    900,
                    "live",
                    0,
                    new Dictionary<string, object?>
                    {
                        ["hfw"] = horizontalFieldWidthMetres,
                        ["frame_count"] = 1,
                        ["image_path"] = @"C:\camera\frame.png"
                    }),
                new LiveImageDecodeResult(
                    new DrawingImage(),
                    100,
                    100,
                    96d,
                    96d,
                    ".png"),
                horizontalFieldWidthMetres
            });
    }

    private static LiveImageExchangeResult ImageExchange(
        int correlationId,
        string requestedPath,
        string responsePath)
    {
        return new LiveImageExchangeResult(
            new EquipmentResponseMessage(
                correlationId,
                "live",
                0,
                new Dictionary<string, object?>
                {
                    ["hfw"] = 1E-3,
                    ["frame_count"] = 1,
                    ["image_path"] = responsePath,
                }),
            requestedPath);
    }

    private sealed class PendingFrameSession : ILiveInteractionSession
    {
        public bool IsBusy { get; private set; }

        public event EventHandler? BusyChanged;

        public TaskCompletionSource<bool> FrameStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> FrameCanceled { get; } = NewSignal();

        public List<double> FrameWidths { get; } = new List<double>();

        public async Task<LiveImageExchangeResult> RequestFrameAsync(
            double horizontalFieldWidthMetres,
            CancellationToken cancellationToken = default)
        {
            FrameWidths.Add(horizontalFieldWidthMetres);
            IsBusy = true;
            BusyChanged?.Invoke(this, EventArgs.Empty);
            FrameStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("An infinite frame wait completed unexpectedly.");
            }
            finally
            {
                IsBusy = false;
                FrameCanceled.TrySetResult(true);
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public Task<EquipmentResponseMessage> MoveStageAsync(
            string moveMode,
            double stageXMetres,
            double stageYMetres,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<EquipmentResponseMessage> MoveCameraAsync(
            string moveMode,
            double cameraXMetres,
            double cameraYMetres,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<EquipmentResponseMessage> FocusAsync(
            double horizontalFieldWidthMetres,
            double rangeMetres,
            int steps,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LiveImageExchangeResult> IntegrateAsync(
            double horizontalFieldWidthMetres,
            int frameCount,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class EquipmentFailureFrameSession : ILiveInteractionSession
    {
        public bool IsBusy => false;

        public event EventHandler? BusyChanged
        {
            add { }
            remove { }
        }

        public int FrameCallCount { get; private set; }

        public Task<LiveImageExchangeResult> RequestFrameAsync(
            double horizontalFieldWidthMetres,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FrameCallCount++;
            return Task.FromException<LiveImageExchangeResult>(
                new LiveEquipmentActionFailedException(
                    new EquipmentResponseMessage(
                        FrameCallCount,
                        "live",
                        1,
                        new Dictionary<string, object?>
                        {
                            ["hfw"] = horizontalFieldWidthMetres,
                            ["frame_count"] = 1,
                            ["image_path"] = @"C:\camera\failed-frame.bmp",
                        })));
        }

        public Task<EquipmentResponseMessage> MoveStageAsync(
            string moveMode,
            double stageXMetres,
            double stageYMetres,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EquipmentResponseMessage> MoveCameraAsync(
            string moveMode,
            double cameraXMetres,
            double cameraYMetres,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EquipmentResponseMessage> FocusAsync(
            double horizontalFieldWidthMetres,
            double rangeMetres,
            int steps,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LiveImageExchangeResult> IntegrateAsync(
            double horizontalFieldWidthMetres,
            int frameCount,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InteractiveMoveSession : ILiveInteractionSession
    {
        private int _activeCalls;
        private int _frameCalls;
        private int _maximumConcurrentEquipmentCalls;

        public bool IsBusy => Volatile.Read(ref _activeCalls) > 0;

        public event EventHandler? BusyChanged;

        public TaskCompletionSource<bool> FirstFrameStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> FirstFrameCanceled { get; } = NewSignal();

        public TaskCompletionSource<bool> SecondFrameStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> SecondFrameCanceled { get; } = NewSignal();

        public TaskCompletionSource<bool> MoveStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> MoveCanceled { get; } = NewSignal();

        public TaskCompletionSource<bool> CaptureStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> CaptureCanceled { get; } = NewSignal();

        public bool BlockMove { get; set; }

        public bool FailMove { get; set; }

        public bool BlockCapture { get; set; }

        public bool FailCapture { get; set; }

        public int FrameCallCount => Volatile.Read(ref _frameCalls);

        public int MoveCallCount { get; private set; }

        public double LastStageMoveXMetres { get; private set; }

        public double LastStageMoveYMetres { get; private set; }

        public double CameraResponseXMetres { get; set; } = -3.2E-9;

        public double CameraResponseYMetres { get; set; } = 7.62E-6;

        public IReadOnlyList<IReadOnlyList<double>> FocusResponseSamples { get; set; } =
            new IReadOnlyList<double>[]
            {
                new[] { 0.1E-6, 500d },
                new[] { 1.5E-6, 600d },
                new[] { 2.1E-6, 1200d }
            };

        public bool MoveStartedAfterFrameCancellation { get; private set; }

        public bool CaptureStartedAfterFrameCancellation { get; private set; }

        public int MaximumConcurrentEquipmentCalls =>
            Volatile.Read(ref _maximumConcurrentEquipmentCalls);

        public async Task<LiveImageExchangeResult> RequestFrameAsync(
            double horizontalFieldWidthMetres,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _frameCalls);
            EnterCall();
            if (call == 1)
            {
                FirstFrameStarted.TrySetResult(true);
            }
            else if (call == 2)
            {
                SecondFrameStarted.TrySetResult(true);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("An infinite frame wait completed unexpectedly.");
            }
            finally
            {
                ExitCall();
                if (call == 1)
                {
                    FirstFrameCanceled.TrySetResult(true);
                }
                else if (call == 2)
                {
                    SecondFrameCanceled.TrySetResult(true);
                }
            }
        }

        public async Task<EquipmentResponseMessage> MoveStageAsync(
            string moveMode,
            double stageXMetres,
            double stageYMetres,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                MoveCallCount++;
                LastStageMoveXMetres = stageXMetres;
                LastStageMoveYMetres = stageYMetres;
                MoveStartedAfterFrameCancellation = FirstFrameCanceled.Task.IsCompleted;
                MoveStarted.TrySetResult(true);
                if (FailMove)
                {
                    throw new InvalidOperationException("Move failed for test.");
                }

                if (BlockMove)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    finally
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            MoveCanceled.TrySetResult(true);
                        }
                    }
                }

                return new EquipmentResponseMessage(
                    700,
                    "stage",
                    0,
                    new Dictionary<string, object?>
                    {
                        ["current_stage_x"] = stageXMetres,
                        ["current_stage_y"] = stageYMetres
                    });
            }
            finally
            {
                ExitCall();
            }
        }

        public Task<EquipmentResponseMessage> MoveCameraAsync(
            string moveMode,
            double cameraXMetres,
            double cameraYMetres,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                return Task.FromResult(
                    new EquipmentResponseMessage(
                        702,
                        "camera",
                        0,
                        new Dictionary<string, object?>
                        {
                            ["current_camera_x"] = CameraResponseXMetres,
                            ["current_camera_y"] = CameraResponseYMetres
                        }));
            }
            finally
            {
                ExitCall();
            }
        }

        public Task<EquipmentResponseMessage> FocusAsync(
            double horizontalFieldWidthMetres,
            double rangeMetres,
            int steps,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                return Task.FromResult(
                    new EquipmentResponseMessage(
                        703,
                        "focus",
                        0,
                        new Dictionary<string, object?>
                        {
                            ["z_to_sharpness_2d"] = FocusResponseSamples
                        }));
            }
            finally
            {
                ExitCall();
            }
        }

        public async Task<LiveImageExchangeResult> IntegrateAsync(
            double horizontalFieldWidthMetres,
            int frameCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                CaptureStartedAfterFrameCancellation = FirstFrameCanceled.Task.IsCompleted;
                CaptureStarted.TrySetResult(true);
                if (FailCapture)
                {
                    throw new LiveEquipmentActionFailedException(
                        new EquipmentResponseMessage(
                            701,
                            "integration",
                            1,
                            new Dictionary<string, object?>
                            {
                                ["hfw"] = horizontalFieldWidthMetres,
                                ["frame_count"] = frameCount,
                                ["image_path"] = @"C:\app-owned\integration-701.bmp",
                            }));
                }

                if (BlockCapture)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    finally
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            CaptureCanceled.TrySetResult(true);
                        }
                    }
                }

                const string path = @"C:\camera\capture.png";
                return new LiveImageExchangeResult(
                    new EquipmentResponseMessage(
                        701,
                        "integration",
                        0,
                        new Dictionary<string, object?>
                        {
                            ["hfw"] = horizontalFieldWidthMetres,
                            ["frame_count"] = frameCount,
                            ["image_path"] = path
                        }),
                    @"C:\app-owned\integration-701.bmp");
            }
            finally
            {
                ExitCall();
            }
        }

        private void EnterCall()
        {
            var active = Interlocked.Increment(ref _activeCalls);
            var observed = Volatile.Read(ref _maximumConcurrentEquipmentCalls);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maximumConcurrentEquipmentCalls,
                    active,
                    observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            BusyChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ExitCall()
        {
            Interlocked.Decrement(ref _activeCalls);
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class BlockingResponseSimulator : IEquipmentResponseSimulator
    {
        public bool BlockActiveRequestRead { get; set; }

        public int ActiveRequestReadCount { get; private set; }

        public TaskCompletionSource<bool> ActiveRequestReadStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> ActiveRequestReadCanceled { get; } = NewSignal();

        public string PayloadFormat => "JSON";

        public Task<EquipmentResponseSimulationDraft> CreateDraftAsync(
            WorkflowNode node,
            int? fallbackCorrelationId,
            CancellationToken cancellationToken,
            string? generatedImagePath = null)
        {
            throw new NotSupportedException();
        }

        public async Task<EquipmentRequestSnapshot?> GetActiveRequestAsync(
            CancellationToken cancellationToken)
        {
            ActiveRequestReadCount++;
            ActiveRequestReadStarted.TrySetResult(true);
            if (!BlockActiveRequestRead)
            {
                return null;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
            finally
            {
                ActiveRequestReadCanceled.TrySetResult(true);
            }
        }

        public Task<FrameResponseSimulationResult> TryPublishFrameResponseAsync(
            EquipmentRequestSnapshot expectedRequest,
            string generatedImagePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ResponsePayloadValidationResult ValidatePayload(string payload)
        {
            throw new NotSupportedException();
        }

        public Task PublishAsync(string payload, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubFileDialogService : IFileDialogService
    {
        public string? ShowOpenWorkflowDialog() => null;

        public string? ShowSaveWorkflowDialog(string suggestedFileName) => null;

        public string? ShowSaveImageDialog(string sourceImagePath, string detectedExtension) => null;

        public string? ShowSelectFolderDialog(string initialFolder) => null;

        public string? ShowSelectLiveImageFolderDialog(string initialFolder) => null;
    }

    private sealed class StubCaptureSnapshotStore : ILiveCaptureSnapshotStore
    {
        public Task<LiveCaptureSnapshot> AcquireAsync(
            string sourceImagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshotPath = System.IO.Path.GetTempFileName();
            File.WriteAllBytes(snapshotPath, new byte[] { 1, 2, 3 });
            return Task.FromResult(new LiveCaptureSnapshot(
                snapshotPath,
                path =>
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }));
        }
    }

    private sealed class StubImageDecoder : ILiveImageDecoder
    {
        public Task<LiveImageDecodeResult> DecodeAsync(
            byte[] encodedImage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LiveImageDecodeResult(
                new DrawingImage(),
                768,
                512,
                96d,
                96d,
                ".bmp"));
        }
    }

    private sealed class StubDefaultFileLauncher : IDefaultFileLauncher
    {
        public string Open(string filePath) => filePath;
    }

    private sealed class StubTemporaryResponseImageService : ITemporaryResponseImageService
    {
        public TemporaryResponseImage CreateTemporaryImage() => throw new NotSupportedException();

        public bool TryReleaseTemporaryImage(string path) => false;
    }

    private sealed class StubExchangeFolderLauncher : IExchangeFolderLauncher
    {
        public string Open() => System.IO.Path.GetTempPath();

        public string Open(string directory) => directory;
    }

    private sealed class StubLocalizationService : ILocalizationService
    {
        public event EventHandler? LanguageChanged;

        public string SelectedLanguage => "en-US";

        public string EffectiveLanguage => "en-US";

        public string this[string key] => key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class StubWorkflowExecutionFacade : IWorkflowExecutionFacade
    {
        public WorkflowRunState State => WorkflowRunState.Idle;

        public WorkflowNode? CurrentNode => null;

        public RunResultStore Results { get; } = new RunResultStore();

        public event EventHandler<WorkflowRunStateChangedEventArgs>? RunStateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<WorkflowNodeStateChangedEventArgs>? NodeStateChanged
        {
            add { }
            remove { }
        }

        public Task RunAsync(WorkflowDocument document) => throw new NotSupportedException();

        public Task RunSelectedAsync(WorkflowDocument document, Guid actionId) =>
            throw new NotSupportedException();

        public void Continue() => throw new NotSupportedException();

        public void Step() => throw new NotSupportedException();

        public void RequestStop() => throw new NotSupportedException();

        public void ForceStop() => throw new NotSupportedException();
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
