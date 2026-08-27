using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        Assert.Equal(10E-3, Assert.Single(session.FrameWidths));
        Assert.True(viewModel.ZoomFrameInCommand.CanExecute(null));

        viewModel.ZoomFrameInCommand.Execute(null);

        await WaitUntilAsync(() => session.FrameWidths.Count >= 2);
        Assert.Equal(5E-3, viewModel.HorizontalFieldWidthMetres);
        Assert.Equal("5", viewModel.HorizontalFieldWidthText);
        Assert.Equal(new[] { 10E-3, 5E-3 }, session.FrameWidths);

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);
        await viewModel.ShutdownAsync();
    }

    [Fact]
    public async Task HfwEditor_AcceptsPositiveScientificValuesWithoutArtificialLimits()
    {
        var viewModel = CreateViewModel(
            new PendingFrameSession(),
            new BlockingResponseSimulator());

        viewModel.HorizontalFieldWidthUnit = "m";
        viewModel.HorizontalFieldWidthText = "2.5E+100";

        Assert.Equal(2.5E+100, viewModel.HorizontalFieldWidthMetres);
        Assert.Empty(viewModel.HorizontalFieldWidthValidationMessage);
        Assert.False(viewModel.ZoomFrameInCommand.CanExecute(null));

        viewModel.Activate();
        Assert.True(viewModel.ZoomFrameInCommand.CanExecute(null));
        viewModel.HorizontalFieldWidthText = "Infinity";
        Assert.NotEmpty(viewModel.HorizontalFieldWidthValidationMessage);
        Assert.False(viewModel.ZoomFrameInCommand.CanExecute(null));

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

        viewModel.HorizontalFieldWidthText = "5";

        Assert.Equal(5E-3, viewModel.HorizontalFieldWidthMetres);
        Assert.Equal(1E-6, viewModel.PixelPitchMetres);
        Assert.Equal("1", viewModel.PixelPitchText);
        Assert.True(viewModel.IsFrameCalibrationPending);
        Assert.False(viewModel.IsDisplayedFrameCalibrationCurrent);
        Assert.False(viewModel.MoveToTargetCommand.CanExecute(target));
        await WaitUntilAsync(() => session.FrameWidths.Count >= 2);

        ApplyDecodedFrame(viewModel, 5E-3);

        Assert.True(viewModel.IsDisplayedFrameCalibrationCurrent);
        Assert.False(viewModel.IsFrameCalibrationPending);
        Assert.True(viewModel.MoveToTargetCommand.CanExecute(target));

        viewModel.StopCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsInteractionActive);
        await viewModel.ShutdownAsync();
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
                RequestFileName = "request.json",
                ResponseFileName = "response.json",
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
                    "return",
                    new Dictionary<string, object?>
                    {
                        ["stage_x"] = 0d,
                        ["stage_y"] = 0d,
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

    private sealed class PendingFrameSession : ILiveInteractionSession
    {
        public bool IsBusy { get; private set; }

        public event EventHandler? BusyChanged;

        public TaskCompletionSource<bool> FrameStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> FrameCanceled { get; } = NewSignal();

        public List<double> FrameWidths { get; } = new List<double>();

        public async Task<EquipmentResponseMessage> RequestFrameAsync(
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

        public Task<EquipmentResponseMessage> MoveRelativeAsync(
            double moveXMetres,
            double moveYMetres,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<EquipmentResponseMessage> CaptureAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
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

        public int FrameCallCount => Volatile.Read(ref _frameCalls);

        public int MoveCallCount { get; private set; }

        public bool MoveStartedAfterFrameCancellation { get; private set; }

        public bool CaptureStartedAfterFrameCancellation { get; private set; }

        public int MaximumConcurrentEquipmentCalls =>
            Volatile.Read(ref _maximumConcurrentEquipmentCalls);

        public async Task<EquipmentResponseMessage> RequestFrameAsync(
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

        public async Task<EquipmentResponseMessage> MoveRelativeAsync(
            double moveXMetres,
            double moveYMetres,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                MoveCallCount++;
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
                    "return",
                    new Dictionary<string, object?>
                    {
                        ["stage_x"] = moveXMetres,
                        ["stage_y"] = moveYMetres
                    });
            }
            finally
            {
                ExitCall();
            }
        }

        public async Task<EquipmentResponseMessage> CaptureAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterCall();
            try
            {
                CaptureStartedAfterFrameCancellation = FirstFrameCanceled.Task.IsCompleted;
                CaptureStarted.TrySetResult(true);
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

                return new EquipmentResponseMessage(
                    701,
                    "return",
                    new Dictionary<string, object?>
                    {
                        ["stage_x"] = 0d,
                        ["stage_y"] = 0d,
                        ["image_path"] = @"C:\camera\capture.png"
                    });
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
    }

    private sealed class StubCaptureSnapshotStore : ILiveCaptureSnapshotStore
    {
        public Task<LiveCaptureSnapshot> AcquireAsync(
            string sourceImagePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubImageDecoder : ILiveImageDecoder
    {
        public Task<LiveImageDecodeResult> DecodeAsync(
            byte[] encodedImage,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
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
