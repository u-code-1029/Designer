using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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

    private sealed class PendingFrameSession : ILiveInteractionSession
    {
        public bool IsBusy { get; private set; }

        public event EventHandler? BusyChanged;

        public TaskCompletionSource<bool> FrameStarted { get; } = NewSignal();

        public TaskCompletionSource<bool> FrameCanceled { get; } = NewSignal();

        public async Task<EquipmentResponseMessage> RequestFrameAsync(
            CancellationToken cancellationToken = default)
        {
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
