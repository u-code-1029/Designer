using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Execution;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Core.Validation;
using DrillFlow.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrillFlow.Desktop.ViewModels;

public sealed class LiveImageTarget
{
    public LiveImageTarget(
        double pixelX,
        double pixelY,
        int imagePixelWidth,
        int imagePixelHeight,
        double moveXMetres,
        double moveYMetres)
    {
        PixelX = pixelX;
        PixelY = pixelY;
        ImagePixelWidth = imagePixelWidth;
        ImagePixelHeight = imagePixelHeight;
        MoveXMetres = moveXMetres;
        MoveYMetres = moveYMetres;
    }

    public double PixelX { get; }

    public double PixelY { get; }

    public int ImagePixelWidth { get; }

    public int ImagePixelHeight { get; }

    public double MoveXMetres { get; }

    public double MoveYMetres { get; }
}

public sealed class LiveInteractionPageViewModel : ObservableObject
{
    private const int MinimumFrameIntervalMilliseconds = 33;
    private const int InitialErrorBackoffMilliseconds = 500;
    private const int MaximumErrorBackoffMilliseconds = 5000;
    private const double DefaultHorizontalFieldWidthMetres = 1E-3d;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ILiveInteractionSession _session;
    private readonly IFileDialogService _fileDialogs;
    private readonly ILiveCaptureSnapshotStore _captureSnapshots;
    private readonly ILiveImageDecoder _imageDecoder;
    private readonly IDefaultFileLauncher _defaultFileLauncher;
    private readonly IEquipmentResponseSimulator _responseSimulator;
    private readonly ITemporaryResponseImageService _temporaryResponseImages;
    private readonly IExchangeFolderLauncher _exchangeFolderLauncher;
    private readonly EquipmentCommunicationOptions _communicationOptions;
    private readonly ILocalizationService _localization;
    private readonly IWorkflowExecutionFacade _workflowExecution;
    private readonly ILogger<LiveInteractionPageViewModel> _logger;
    private readonly object _streamSync = new();
    private readonly object _simulationSync = new();
    private readonly SemaphoreSlim _simulationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private Task _streamLoopTask = Task.CompletedTask;
    private Task _continuousResponseTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private CancellationTokenSource? _streamDelayCancellation;
    private CancellationTokenSource? _capturePostResponseCancellation;
    private CancellationTokenSource? _moveOperationCancellation;
    private CancellationTokenSource? _continuousResponseCancellation;
    private bool _isPageActive;
    private bool _isStreamingRequested;
    private bool _isStreaming;
    private bool _isExclusiveOperation;
    private bool _isMoving;
    private bool _isCapturing;
    private bool _invertXAxis;
    private bool _invertYAxis;
    private string _horizontalFieldWidthText = "1";
    private string _horizontalFieldWidthUnit = "mm";
    private double _horizontalFieldWidthMetres = DefaultHorizontalFieldWidthMetres;
    private string _horizontalFieldWidthValidationMessage = string.Empty;
    private string _pixelPitchText = string.Empty;
    private string _pixelPitchUnit = "m";
    private double _pixelPitchMetres;
    private string _pixelPitchValidationMessage = string.Empty;
    private string _stageMoveMode = LiveInteractionProtocol.RelativeMoveMode;
    private string _stageInputXText = "0E0";
    private string _stageInputYText = "0E0";
    private string _stageMoveValidationMessage = string.Empty;
    private string _cameraMoveMode = LiveInteractionProtocol.RelativeMoveMode;
    private string _cameraInputXText = "0E0";
    private string _cameraInputYText = "0E0";
    private string _cameraMoveValidationMessage = string.Empty;
    private string _focusHfwText = "1E-3";
    private string _focusRangeText = "50E-6";
    private string _focusStepsText = "13";
    private string _focusValidationMessage = string.Empty;
    private int _integrationFrameCount = 8;
    private ImageSource? _liveImageSource;
    private string _imagePath = string.Empty;
    private int _imagePixelWidth;
    private int _imagePixelHeight;
    private double _imageDpiX = 96d;
    private double _imageDpiY = 96d;
    private bool _isDisplayedFrameCalibrationCurrent;
    private long _frameCount;
    private DateTime? _lastFrameAt;
    private double _stageX;
    private double _stageY;
    private double _cameraX;
    private double _cameraY;
    private bool _hasCameraPosition;
    private string _focusResultText = "-";
    private int _lastCorrelationId;
    private bool _hasStagePosition;
    private bool _hasTarget;
    private bool _isTargetMarkerVisible;
    private double _targetPixelX;
    private double _targetPixelY;
    private double _targetMoveXMetres;
    private double _targetMoveYMetres;
    private string _savedImagePath = string.Empty;
    private string _statusKey = "LiveStatusReady";
    private object[] _statusArguments = Array.Empty<object>();
    private bool _statusIsError;
    private bool _statusIsWarning;
    private bool _isShuttingDown;
    private bool _resumeAfterWorkflow;
    private bool _restartWhenStreamStops;
    private bool _isContinuousResponseGenerationEnabled;
    private string? _retainedSimulationImagePath;
    private int? _retainedSimulationCorrelationId;
    private long _generatedTestFrameCount;
    private int _activationGeneration;

    public LiveInteractionPageViewModel(
        ILiveInteractionSession session,
        IFileDialogService fileDialogs,
        ILiveCaptureSnapshotStore captureSnapshots,
        ILiveImageDecoder imageDecoder,
        IDefaultFileLauncher defaultFileLauncher,
        IEquipmentResponseSimulator responseSimulator,
        ITemporaryResponseImageService temporaryResponseImages,
        IExchangeFolderLauncher exchangeFolderLauncher,
        IOptions<EquipmentCommunicationOptions> communicationOptions,
        ILocalizationService localization,
        IWorkflowExecutionFacade workflowExecution,
        ILogger<LiveInteractionPageViewModel> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _captureSnapshots = captureSnapshots ?? throw new ArgumentNullException(nameof(captureSnapshots));
        _imageDecoder = imageDecoder ?? throw new ArgumentNullException(nameof(imageDecoder));
        _defaultFileLauncher = defaultFileLauncher
            ?? throw new ArgumentNullException(nameof(defaultFileLauncher));
        _responseSimulator = responseSimulator
            ?? throw new ArgumentNullException(nameof(responseSimulator));
        _temporaryResponseImages = temporaryResponseImages
            ?? throw new ArgumentNullException(nameof(temporaryResponseImages));
        _exchangeFolderLauncher = exchangeFolderLauncher
            ?? throw new ArgumentNullException(nameof(exchangeFolderLauncher));
        _communicationOptions = communicationOptions?.Value
            ?? throw new ArgumentNullException(nameof(communicationOptions));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _workflowExecution = workflowExecution ?? throw new ArgumentNullException(nameof(workflowExecution));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        StartCommand = new RelayCommand(StartStreaming, CanStartStreaming);
        StopCommand = new RelayCommand(
            StopStreaming,
            () => IsStreamingRequested || _restartWhenStreamStops);
        CaptureCommand = new AsyncRelayCommand(CaptureAsync, CanCapture);
        ExecuteStageMoveCommand = new AsyncRelayCommand(ExecuteStageMoveAsync, CanExecuteStageMove);
        ExecuteCameraMoveCommand = new AsyncRelayCommand(ExecuteCameraMoveAsync, CanExecuteCameraMove);
        ExecuteFocusCommand = new AsyncRelayCommand(ExecuteFocusAsync, CanExecuteFocus);
        MoveToTargetCommand = new AsyncRelayCommand<LiveImageTarget>(MoveToTargetAsync, CanMoveToTarget);
        OpenSavedImageCommand = new RelayCommand(OpenSavedImage, CanOpenSavedImage);
        OpenExchangeFolderCommand = new RelayCommand(OpenExchangeFolder, () => !_isShuttingDown);
        GenerateSingleFrameResponseCommand = new AsyncRelayCommand(
            GenerateSingleFrameResponseAsync,
            CanGenerateSingleFrameResponse);
        ZoomFrameInCommand = new RelayCommand(ZoomFrameIn, CanZoomFrameIn);
        ZoomFrameOutCommand = new RelayCommand(ZoomFrameOut, CanZoomFrameOut);

        _localization.LanguageChanged += OnLanguageChanged;
        _session.BusyChanged += OnSessionBusyChanged;
        _workflowExecution.RunStateChanged += OnWorkflowRunStateChanged;
        ValidateHorizontalFieldWidth(restartStreaming: false);
        ValidatePixelPitch();
        ValidateStageMove();
        ValidateCameraMove();
        ValidateFocus();
    }

    public IRelayCommand StartCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IAsyncRelayCommand CaptureCommand { get; }

    public IAsyncRelayCommand ExecuteStageMoveCommand { get; }

    public IAsyncRelayCommand ExecuteCameraMoveCommand { get; }

    public IAsyncRelayCommand ExecuteFocusCommand { get; }

    public IAsyncRelayCommand<LiveImageTarget> MoveToTargetCommand { get; }

    public IRelayCommand OpenSavedImageCommand { get; }

    public IRelayCommand OpenExchangeFolderCommand { get; }

    public IAsyncRelayCommand GenerateSingleFrameResponseCommand { get; }

    /// <summary>Halves HFW, increasing optical magnification for subsequent frames.</summary>
    public IRelayCommand ZoomFrameInCommand { get; }

    /// <summary>Doubles HFW, decreasing optical magnification for subsequent frames.</summary>
    public IRelayCommand ZoomFrameOutCommand { get; }

    /// <summary>
    /// When enabled, each distinct active frame request receives at most one generated response.
    /// Setting this property is safe to use as a two-way ToggleSwitch binding.
    /// </summary>
    public bool IsContinuousResponseGenerationEnabled
    {
        get => _isContinuousResponseGenerationEnabled;
        set
        {
            if (value && _isShuttingDown)
            {
                return;
            }

            if (!SetProperty(ref _isContinuousResponseGenerationEnabled, value))
            {
                return;
            }

            GenerateSingleFrameResponseCommand.NotifyCanExecuteChanged();
            if (value)
            {
                StartContinuousResponseGeneration();
            }
            else
            {
                StopContinuousResponseGeneration();
            }
        }
    }

    public long GeneratedTestFrameCount
    {
        get => _generatedTestFrameCount;
        private set => SetProperty(ref _generatedTestFrameCount, value);
    }

    public bool IsStreamingRequested
    {
        get => _isStreamingRequested;
        private set
        {
            if (SetProperty(ref _isStreamingRequested, value))
            {
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(IsInteractionActive));
                RefreshCommands();
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsInteractionActive));
            }
        }
    }

    public bool IsMoving
    {
        get => _isMoving;
        private set
        {
            if (SetProperty(ref _isMoving, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsInteractionActive));
            }
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsInteractionActive));
            }
        }
    }

    public bool IsBusy =>
        IsStreaming || IsMoving || IsCapturing || _isExclusiveOperation || _restartWhenStreamStops;

    public bool IsInteractionActive =>
        IsStreamingRequested
        || IsStreaming
        || IsMoving
        || IsCapturing
        || _isExclusiveOperation
        || _restartWhenStreamStops;

    public bool IsLive => IsStreamingRequested && !IsMoving && !IsCapturing;

    public double HorizontalFieldWidthMetres => _horizontalFieldWidthMetres;

    public string HorizontalFieldWidthText
    {
        get => _horizontalFieldWidthText;
        set
        {
            if (SetProperty(ref _horizontalFieldWidthText, value ?? string.Empty))
            {
                ValidateHorizontalFieldWidth(restartStreaming: true);
                RefreshCommands();
            }
        }
    }

    public string HorizontalFieldWidthUnit
    {
        get => _horizontalFieldWidthUnit;
        set
        {
            var normalized = NormalizeUnit(value);
            if (string.Equals(_horizontalFieldWidthUnit, normalized, StringComparison.Ordinal))
            {
                return;
            }

            var hadValidWidth = TryParsePositiveLength(
                _horizontalFieldWidthText,
                _horizontalFieldWidthUnit,
                out var metres)
                && LiveInteractionProtocol.IsValidHorizontalFieldWidth(metres);
            _horizontalFieldWidthUnit = normalized;
            OnPropertyChanged();
            if (hadValidWidth)
            {
                _horizontalFieldWidthMetres = metres;
                _horizontalFieldWidthText = FormatLength(metres, normalized);
                OnPropertyChanged(nameof(HorizontalFieldWidthText));
            }

            ValidateHorizontalFieldWidth(restartStreaming: false);
            RefreshCommands();
        }
    }

    public string HorizontalFieldWidthValidationMessage
    {
        get => _horizontalFieldWidthValidationMessage;
        private set => SetProperty(ref _horizontalFieldWidthValidationMessage, value);
    }

    public int IntegrationFrameCount
    {
        get => _integrationFrameCount;
        set
        {
            if (SetProperty(ref _integrationFrameCount, value))
            {
                RefreshCommands();
            }
        }
    }

    public int[] IntegrationFrameCountOptions { get; } = { 1, 2, 4, 8, 16, 32, 64 };

    public string StageMoveMode
    {
        get => _stageMoveMode;
        set
        {
            if (SetProperty(ref _stageMoveMode, value ?? string.Empty))
            {
                ValidateStageMove();
            }
        }
    }

    public string StageInputXText
    {
        get => _stageInputXText;
        set
        {
            if (SetProperty(ref _stageInputXText, value ?? string.Empty))
            {
                ValidateStageMove();
            }
        }
    }

    public string StageInputYText
    {
        get => _stageInputYText;
        set
        {
            if (SetProperty(ref _stageInputYText, value ?? string.Empty))
            {
                ValidateStageMove();
            }
        }
    }

    public string StageMoveValidationMessage
    {
        get => _stageMoveValidationMessage;
        private set => SetProperty(ref _stageMoveValidationMessage, value);
    }

    public string CameraMoveMode
    {
        get => _cameraMoveMode;
        set
        {
            if (SetProperty(ref _cameraMoveMode, value ?? string.Empty))
            {
                ValidateCameraMove();
            }
        }
    }

    public string CameraInputXText
    {
        get => _cameraInputXText;
        set
        {
            if (SetProperty(ref _cameraInputXText, value ?? string.Empty))
            {
                ValidateCameraMove();
            }
        }
    }

    public string CameraInputYText
    {
        get => _cameraInputYText;
        set
        {
            if (SetProperty(ref _cameraInputYText, value ?? string.Empty))
            {
                ValidateCameraMove();
            }
        }
    }

    public string CameraMoveValidationMessage
    {
        get => _cameraMoveValidationMessage;
        private set => SetProperty(ref _cameraMoveValidationMessage, value);
    }

    public string FocusHfwText
    {
        get => _focusHfwText;
        set
        {
            if (SetProperty(ref _focusHfwText, value ?? string.Empty))
            {
                ValidateFocus();
            }
        }
    }

    public string FocusRangeText
    {
        get => _focusRangeText;
        set
        {
            if (SetProperty(ref _focusRangeText, value ?? string.Empty))
            {
                ValidateFocus();
            }
        }
    }

    public string FocusStepsText
    {
        get => _focusStepsText;
        set
        {
            if (SetProperty(ref _focusStepsText, value ?? string.Empty))
            {
                ValidateFocus();
            }
        }
    }

    public string FocusValidationMessage
    {
        get => _focusValidationMessage;
        private set => SetProperty(ref _focusValidationMessage, value);
    }

    public double PixelPitchMetres => _pixelPitchMetres;

    public int XAxisSign => InvertXAxis ? -1 : 1;

    public int YAxisSign => InvertYAxis ? -1 : 1;

    public bool InvertXAxis
    {
        get => _invertXAxis;
        set => SetProperty(ref _invertXAxis, value);
    }

    public bool InvertYAxis
    {
        get => _invertYAxis;
        set => SetProperty(ref _invertYAxis, value);
    }

    public string PixelPitchText
    {
        get => _pixelPitchText;
        set
        {
            if (SetProperty(ref _pixelPitchText, value ?? string.Empty))
            {
                ValidatePixelPitch();
                RefreshCommands();
            }
        }
    }

    public string PixelPitchUnit
    {
        get => _pixelPitchUnit;
        set
        {
            var normalized = NormalizeUnit(value);
            if (string.Equals(_pixelPitchUnit, normalized, StringComparison.Ordinal))
            {
                return;
            }

            var hadValidPitch = TryParsePitch(_pixelPitchText, _pixelPitchUnit, out var metres);
            _pixelPitchUnit = normalized;
            OnPropertyChanged();
            if (hadValidPitch)
            {
                _pixelPitchMetres = metres;
                _pixelPitchText = FormatPitch(metres, normalized);
                OnPropertyChanged(nameof(PixelPitchText));
            }

            ValidatePixelPitch();
            RefreshCommands();
        }
    }

    public string PixelPitchValidationMessage
    {
        get => _pixelPitchValidationMessage;
        private set => SetProperty(ref _pixelPitchValidationMessage, value);
    }

    public ImageSource? LiveImageSource
    {
        get => _liveImageSource;
        private set
        {
            if (SetProperty(ref _liveImageSource, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(IsFrameCalibrationPending));
                RefreshCommands();
            }
        }
    }

    public bool HasImage => LiveImageSource is not null;

    /// <summary>
    /// True only when the displayed image was requested with the current HFW. Movement remains
    /// disabled while an older image is visible after a magnification change.
    /// </summary>
    public bool IsDisplayedFrameCalibrationCurrent
    {
        get => _isDisplayedFrameCalibrationCurrent;
        private set
        {
            if (SetProperty(ref _isDisplayedFrameCalibrationCurrent, value))
            {
                OnPropertyChanged(nameof(IsFrameCalibrationPending));
                MoveToTargetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsFrameCalibrationPending => HasImage && !IsDisplayedFrameCalibrationCurrent;

    public string ImagePath
    {
        get => _imagePath;
        private set => SetProperty(ref _imagePath, value);
    }

    public int ImagePixelWidth
    {
        get => _imagePixelWidth;
        private set
        {
            if (SetProperty(ref _imagePixelWidth, value))
            {
                OnPropertyChanged(nameof(ImageDimensionsText));
            }
        }
    }

    public int ImagePixelHeight
    {
        get => _imagePixelHeight;
        private set
        {
            if (SetProperty(ref _imagePixelHeight, value))
            {
                OnPropertyChanged(nameof(ImageDimensionsText));
            }
        }
    }

    public double ImageDpiX
    {
        get => _imageDpiX;
        private set => SetProperty(ref _imageDpiX, value);
    }

    public double ImageDpiY
    {
        get => _imageDpiY;
        private set => SetProperty(ref _imageDpiY, value);
    }

    public string ImageDimensionsText => ImagePixelWidth > 0 && ImagePixelHeight > 0
        ? string.Format(CultureInfo.CurrentCulture, "{0} × {1} px", ImagePixelWidth, ImagePixelHeight)
        : "-";

    public long FrameCount
    {
        get => _frameCount;
        private set => SetProperty(ref _frameCount, value);
    }

    public DateTime? LastFrameAt
    {
        get => _lastFrameAt;
        private set
        {
            if (SetProperty(ref _lastFrameAt, value))
            {
                OnPropertyChanged(nameof(LastFrameAtText));
            }
        }
    }

    public string LastFrameAtText => LastFrameAt?.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture) ?? "-";

    public string StageXText => HasStagePosition ? _stageX.ToString("0.########E+0", CultureInfo.CurrentCulture) + " m" : "-";

    public string StageYText => HasStagePosition ? _stageY.ToString("0.########E+0", CultureInfo.CurrentCulture) + " m" : "-";

    public string CameraXText => HasCameraPosition
        ? _cameraX.ToString("0.########E+0", CultureInfo.CurrentCulture) + " m"
        : "-";

    public string CameraYText => HasCameraPosition
        ? _cameraY.ToString("0.########E+0", CultureInfo.CurrentCulture) + " m"
        : "-";

    public bool HasCameraPosition
    {
        get => _hasCameraPosition;
        private set
        {
            if (SetProperty(ref _hasCameraPosition, value))
            {
                OnPropertyChanged(nameof(CameraXText));
                OnPropertyChanged(nameof(CameraYText));
            }
        }
    }

    public string FocusResultText
    {
        get => _focusResultText;
        private set => SetProperty(ref _focusResultText, value);
    }

    public int LastCorrelationId
    {
        get => _lastCorrelationId;
        private set => SetProperty(ref _lastCorrelationId, value);
    }

    public bool HasStagePosition
    {
        get => _hasStagePosition;
        private set
        {
            if (SetProperty(ref _hasStagePosition, value))
            {
                OnPropertyChanged(nameof(StageXText));
                OnPropertyChanged(nameof(StageYText));
            }
        }
    }

    public bool HasTarget
    {
        get => _hasTarget;
        private set => SetProperty(ref _hasTarget, value);
    }

    public bool IsTargetMarkerVisible
    {
        get => _isTargetMarkerVisible;
        private set => SetProperty(ref _isTargetMarkerVisible, value);
    }

    public double TargetPixelX
    {
        get => _targetPixelX;
        private set => SetProperty(ref _targetPixelX, value);
    }

    public double TargetPixelY
    {
        get => _targetPixelY;
        private set => SetProperty(ref _targetPixelY, value);
    }

    public string TargetPixelText => HasTarget
        ? string.Format(CultureInfo.CurrentCulture, "({0:0.##}, {1:0.##}) px", TargetPixelX, TargetPixelY)
        : "-";

    public string TargetMoveText => HasTarget
        ? string.Format(
            CultureInfo.CurrentCulture,
            "X {0:0.########E+0} m / Y {1:0.########E+0} m",
            _targetMoveXMetres,
            _targetMoveYMetres)
        : "-";

    public string SavedImagePath
    {
        get => _savedImagePath;
        private set
        {
            if (SetProperty(ref _savedImagePath, value))
            {
                OpenSavedImageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage => FormatLocalized(_statusKey, _statusArguments);

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool StatusIsWarning
    {
        get => _statusIsWarning;
        private set => SetProperty(ref _statusIsWarning, value);
    }

    public void Activate()
    {
        if (_isPageActive || _isShuttingDown)
        {
            return;
        }

        _isPageActive = true;
        _activationGeneration++;
        if (IsWorkflowBusy())
        {
            _resumeAfterWorkflow = true;
            SetStatusWarning("LiveStatusWorkflowBusy");
            RefreshCommands();
            return;
        }

        if (IsStreaming || _isExclusiveOperation)
        {
            SetRestartWhenStreamStops(true);
            SetStatus("LiveStatusConnecting");
            return;
        }

        StartStreaming();
    }

    public void Deactivate()
    {
        _isPageActive = false;
        _activationGeneration++;
        _capturePostResponseCancellation?.Cancel();
        _moveOperationCancellation?.Cancel();
        _resumeAfterWorkflow = false;
        SetRestartWhenStreamStops(false);
        IsContinuousResponseGenerationEnabled = false;
        StopStreaming();
    }

    public bool TryCreateMoveTarget(
        double viewportWidthDip,
        double viewportHeightDip,
        double clickXDip,
        double clickYDip,
        out LiveImageTarget? target)
    {
        target = null;
        if (!HasImage)
        {
            return false;
        }

        if (!IsDisplayedFrameCalibrationCurrent)
        {
            SetStatusWarning("LiveStatusFrameCalibrationPending");
            return false;
        }

        if (!TryParsePitch(PixelPitchText, PixelPitchUnit, out var pitchMetres))
        {
            ValidatePixelPitch();
            return false;
        }

        try
        {
            if (!LiveImageCoordinateMapper.TryMapToRelativeMove(
                    ImagePixelWidth,
                    ImagePixelHeight,
                    ImageDpiX,
                    ImageDpiY,
                    viewportWidthDip,
                    viewportHeightDip,
                    clickXDip,
                    clickYDip,
                    pitchMetres,
                    XAxisSign,
                    YAxisSign,
                    out var mapped))
            {
                SetStatusWarning("LiveStatusOutsideImage");
                return false;
            }

            target = new LiveImageTarget(
                mapped.SourceX,
                mapped.SourceY,
                ImagePixelWidth,
                ImagePixelHeight,
                mapped.MoveXMetres,
                mapped.MoveYMetres);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException || exception is ParameterValidationException)
        {
            SetStatusError("LiveStatusMoveFailed", exception.Message);
            return false;
        }
    }

    public Task ShutdownAsync()
    {
        lock (_streamSync)
        {
            if (_shutdownTask is null)
            {
                _shutdownTask = ShutdownCoreAsync();
            }

            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        _isShuttingDown = true;
        _isPageActive = false;
        _activationGeneration++;
        SetRestartWhenStreamStops(false);
        IsStreamingRequested = false;
        if (_isContinuousResponseGenerationEnabled)
        {
            _isContinuousResponseGenerationEnabled = false;
            OnPropertyChanged(nameof(IsContinuousResponseGenerationEnabled));
        }

        lock (_streamSync)
        {
            _streamDelayCancellation?.Cancel();
        }

        lock (_simulationSync)
        {
            _continuousResponseCancellation?.Cancel();
        }

        // Shutdown cancels every app-owned Live operation. Navigation uses operation-specific
        // live/stage/camera/focus/integration tokens; Stop owns only the active live-image loop.
        _shutdownCancellation.Cancel();

        Task streamTask;
        Task continuousResponseTask;
        lock (_streamSync)
        {
            streamTask = _streamLoopTask;
        }

        lock (_simulationSync)
        {
            continuousResponseTask = _continuousResponseTask;
        }

        var captureTask = CaptureCommand.ExecutionTask ?? Task.CompletedTask;
        var moveTask = MoveToTargetCommand.ExecutionTask ?? Task.CompletedTask;
        var stageMoveTask = ExecuteStageMoveCommand.ExecutionTask ?? Task.CompletedTask;
        var cameraMoveTask = ExecuteCameraMoveCommand.ExecutionTask ?? Task.CompletedTask;
        var focusTask = ExecuteFocusCommand.ExecutionTask ?? Task.CompletedTask;
        var singleFrameResponseTask = GenerateSingleFrameResponseCommand.ExecutionTask
                                      ?? Task.CompletedTask;
        var pendingOperations = Task.WhenAll(
            streamTask,
            continuousResponseTask,
            captureTask,
            moveTask,
            stageMoveTask,
            cameraMoveTask,
            focusTask,
            singleFrameResponseTask);
        var completedWithinBudget = await LiveInteractionShutdownDrain
            .WaitForCompletionAsync(pendingOperations, ShutdownDrainTimeout);

        _session.BusyChanged -= OnSessionBusyChanged;
        _workflowExecution.RunStateChanged -= OnWorkflowRunStateChanged;
        _localization.LanguageChanged -= OnLanguageChanged;

        if (!completedWithinBudget)
        {
            TryLogShutdownWarning(
                "Live interaction shutdown did not drain within {ShutdownDrainTimeout}; "
                + "the blocked operating-system I/O will be observed in the background.",
                ShutdownDrainTimeout);
            _ = ObserveShutdownOperationsAsync(
                pendingOperations,
                _shutdownCancellation,
                _logger);
            ReleaseRetainedSimulationImage();
            return;
        }

        await ObserveShutdownOperationsAsync(
            pendingOperations,
            _shutdownCancellation,
            _logger);
        ReleaseRetainedSimulationImage();
    }

    private void TryLogShutdownWarning(string message, params object[] arguments)
    {
        try
        {
            _logger.LogWarning(message, arguments);
        }
        catch (Exception)
        {
            // The host can already be disposing logging providers after a bounded abandon.
        }
    }

    private static async Task ObserveShutdownOperationsAsync(
        Task pendingOperations,
        CancellationTokenSource shutdownCancellation,
        ILogger logger)
    {
        try
        {
            await pendingOperations.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during application shutdown.
        }
        catch (Exception exception)
        {
            try
            {
                logger.LogWarning(exception, "A live interaction operation failed during shutdown.");
            }
            catch (Exception)
            {
                // A late OS completion can arrive after the host disposed its logger providers.
            }
        }
        finally
        {
            shutdownCancellation.Dispose();
        }
    }

    private bool CanStartStreaming() =>
        _isPageActive
        && !IsStreamingRequested
        && !IsStreaming
        && !_isExclusiveOperation
        && string.IsNullOrEmpty(HorizontalFieldWidthValidationMessage)
        && !IsWorkflowBusy();

    private void StartStreaming()
    {
        if (!CanStartStreaming())
        {
            return;
        }

        lock (_streamSync)
        {
            if (!_streamLoopTask.IsCompleted)
            {
                return;
            }

            IsStreamingRequested = true;
            SetStatus("LiveStatusConnecting");
            _streamDelayCancellation?.Dispose();
            _streamDelayCancellation = new CancellationTokenSource();
            _streamLoopTask = RunFrameLoopAsync(_streamDelayCancellation.Token);
        }
    }

    private void StopStreaming()
    {
        SetRestartWhenStreamStops(false);
        IsStreamingRequested = false;
        lock (_streamSync)
        {
            // The same lifecycle token owns request publication, response waiting, image I/O,
            // and throttling. FileEquipmentTransport verifies the exact correlation, command,
            // and payload before asynchronously deleting a request canceled by the first Stop.
            _streamDelayCancellation?.Cancel();
        }

        if (!_isExclusiveOperation)
        {
            SetStatus(IsStreaming ? "LiveStatusStopping" : "LiveStatusStopped");
        }
    }

    private async Task RunFrameLoopAsync(CancellationToken delayCancellation)
    {
        using var postResponseCancellation = LiveInteractionCancellation.CreatePostResponseSource(
            delayCancellation,
            _shutdownCancellation.Token);
        IsStreaming = true;
        var consecutiveErrors = 0;
        var preserveTerminalStatus = false;
        try
        {
            while (_isPageActive && IsStreamingRequested)
            {
                try
                {
                    var requestedHorizontalFieldWidthMetres = _horizontalFieldWidthMetres;
                    var exchange = await _session.RequestFrameAsync(
                        requestedHorizontalFieldWidthMetres,
                        postResponseCancellation.Token);
                    var response = exchange.Response;
                    if (_isShuttingDown)
                    {
                        TryDeleteOwnedResponseImage(exchange);
                        break;
                    }

                    ApplyCorrelationResponse(response);
                    if (!_isPageActive || !IsStreamingRequested)
                    {
                        TryDeleteOwnedResponseImage(exchange);
                        break;
                    }

                    // Stop/navigation owns both the frame exchange and image-path I/O. A canceled
                    // published request is cleaned only if it still matches this exchange.
                    LiveImageDecodeResult image;
                    try
                    {
                        using (var imageIoTimeout = LiveImageIoTimeout.CreateSource(
                                   _communicationOptions.ResponseTimeout))
                        using (var imageIoCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                                   postResponseCancellation.Token,
                                   imageIoTimeout.Token))
                        {
                            try
                            {
                                image = await LiveImageFileLoader.LoadAsync(
                                    response.ImagePath!,
                                    _imageDecoder,
                                    imageIoCancellation.Token);
                            }
                            catch (OperationCanceledException exception) when (
                                LiveImageIoTimeout.IsTimeout(
                                    imageIoTimeout,
                                    postResponseCancellation.Token))
                            {
                                throw LiveImageIoTimeout.CreateException(
                                    _communicationOptions.ResponseTimeout,
                                    exception);
                            }
                        }
                    }
                    finally
                    {
                        // A controller can return a different path than the app requested. Only
                        // the unique, correlation-owned pathname is ours to remove.
                        TryDeleteOwnedResponseImage(exchange);
                    }
                    if (!_isPageActive || !IsStreamingRequested)
                    {
                        break;
                    }

                    ApplyImageResponse(
                        response,
                        image,
                        requestedHorizontalFieldWidthMetres);
                    FrameCount++;
                    LastFrameAt = DateTime.Now;
                    consecutiveErrors = 0;
                    SetStatus("LiveStatusStreaming", FrameCount);
                    await DelayIfStillStreamingAsync(MinimumFrameIntervalMilliseconds, delayCancellation);
                }
                catch (OperationCanceledException) when (
                    delayCancellation.IsCancellationRequested || _shutdownCancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (LiveEquipmentActionFailedException exception)
                {
                    if (!_isPageActive || !IsStreamingRequested)
                    {
                        break;
                    }

                    // A controller result is an explicit terminal decision, not a transient file
                    // or image-I/O failure. Stop issuing frames until the operator starts again.
                    _logger.LogError(
                        exception,
                        "Live frame request was rejected by equipment; automatic retry stopped.");
                    SetRestartWhenStreamStops(false);
                    IsStreamingRequested = false;
                    preserveTerminalStatus = true;
                    SetStatusError("LiveStatusFrameFailed", exception.Message);
                    break;
                }
                catch (Exception exception)
                {
                    if (!_isPageActive || !IsStreamingRequested)
                    {
                        break;
                    }

                    consecutiveErrors++;
                    var backoff = Math.Min(
                        InitialErrorBackoffMilliseconds * Math.Pow(2d, Math.Min(consecutiveErrors - 1, 4)),
                        MaximumErrorBackoffMilliseconds);
                    var delay = (int)backoff;
                    _logger.LogWarning(exception, "Live frame request failed; retrying in {DelayMilliseconds} ms.", delay);
                    SetStatusWarning("LiveStatusFrameRetry", delay, exception.Message);
                    await DelayIfStillStreamingAsync(delay, delayCancellation);
                }
            }
        }
        finally
        {
            IsStreaming = false;
            RefreshCommands();
            if (_restartWhenStreamStops
                && _isPageActive
                && !_isExclusiveOperation
                && !IsWorkflowBusy()
                && !_isShuttingDown)
            {
                Task completedLoop;
                lock (_streamSync)
                {
                    completedLoop = _streamLoopTask;
                }

                RestartStreamingAfterLoopCompletes(completedLoop);
            }
            else if (!IsStreamingRequested
                     && !_isExclusiveOperation
                     && !IsWorkflowBusy()
                     && !_isShuttingDown
                     && !preserveTerminalStatus)
            {
                SetStatus("LiveStatusStopped");
            }
        }
    }

    private async void RestartStreamingAfterLoopCompletes(Task completedLoop)
    {
        try
        {
            await completedLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The previous live frame loop ended while restarting.");
        }

        if (!_restartWhenStreamStops)
        {
            return;
        }

        if (_isPageActive
            && !_isExclusiveOperation
            && !IsWorkflowBusy()
            && !_isShuttingDown)
        {
            SetRestartWhenStreamStops(false);
            _resumeAfterWorkflow = false;
            StartStreaming();
        }
        else if (!_isPageActive || _isShuttingDown)
        {
            SetRestartWhenStreamStops(false);
        }
    }

    private async Task DelayIfStillStreamingAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (_isPageActive && IsStreamingRequested)
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
    }

    private void OpenExchangeFolder()
    {
        try
        {
            var openedPath = _exchangeFolderLauncher.Open();
            SetStatus("LiveStatusExchangeFolderOpened", openedPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Opening the live equipment exchange folder failed.");
            SetStatusError("LiveStatusExchangeFolderOpenFailed", exception.Message);
        }
    }

    private bool CanGenerateSingleFrameResponse()
    {
        return !_isShuttingDown && !IsContinuousResponseGenerationEnabled;
    }

    private async Task GenerateSingleFrameResponseAsync()
    {
        if (!CanGenerateSingleFrameResponse())
        {
            return;
        }

        try
        {
            var result = await GenerateMatchingFrameResponseAsync(
                null,
                _shutdownCancellation.Token);
            ReportFrameSimulationResult(result);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Generating a single live frame response failed.");
            SetStatusError("LiveStatusTestFrameResponseFailed", exception.Message);
        }
    }

    private async void StartContinuousResponseGeneration()
    {
        Task previousTask;
        lock (_simulationSync)
        {
            previousTask = _continuousResponseTask;
        }

        try
        {
            await previousTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The previous continuous frame responder ended unexpectedly.");
        }

        if (!IsContinuousResponseGenerationEnabled || _isShuttingDown)
        {
            return;
        }

        lock (_simulationSync)
        {
            if (!_continuousResponseTask.IsCompleted)
            {
                return;
            }

            _continuousResponseCancellation?.Dispose();
            _continuousResponseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCancellation.Token);
            _continuousResponseTask = RunContinuousResponseGenerationAsync(
                _continuousResponseCancellation.Token);
        }
    }

    private void StopContinuousResponseGeneration()
    {
        lock (_simulationSync)
        {
            _continuousResponseCancellation?.Cancel();
        }
    }

    private async Task RunContinuousResponseGenerationAsync(CancellationToken cancellationToken)
    {
        int? handledCorrelationId = null;
        var pollingMilliseconds = Math.Max(
            MinimumFrameIntervalMilliseconds,
            (int)Math.Min(int.MaxValue, _communicationOptions.PollingInterval.TotalMilliseconds));
        try
        {
            while (IsContinuousResponseGenerationEnabled && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var request = await _responseSimulator.GetActiveRequestAsync(cancellationToken);
                    if (request != null
                        && string.Equals(
                            request.Action,
                            LiveInteractionProtocol.LiveAction,
                            StringComparison.Ordinal)
                        && handledCorrelationId != request.CorrelationId)
                    {
                        var result = await GenerateMatchingFrameResponseAsync(
                            request,
                            cancellationToken);
                        if (result.Status == FrameResponseSimulationStatus.Published
                            || result.Status == FrameResponseSimulationStatus.ResponseAlreadyExists)
                        {
                            // A controller response already present for this correlation wins. Do
                            // not retry it or replace it on every poll.
                            handledCorrelationId = request.CorrelationId;
                        }
                        else if (result.Status == FrameResponseSimulationStatus.ActiveRequestChanged)
                        {
                            handledCorrelationId = null;
                        }
                    }

                    await Task.Delay(pollingMilliseconds, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Continuous live frame response generation failed; polling will continue.");
                    SetStatusError("LiveStatusTestFrameResponseFailed", exception.Message);
                    await Task.Delay(InitialErrorBackoffMilliseconds, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<FrameResponseSimulationResult> GenerateMatchingFrameResponseAsync(
        EquipmentRequestSnapshot? observedRequest,
        CancellationToken cancellationToken)
    {
        var responsePath = Path.Combine(
            _communicationOptions.ExchangeDirectory,
            _communicationOptions.ResponseFileName);
        var request = observedRequest
                      ?? await _responseSimulator.GetActiveRequestAsync(cancellationToken);
        if (request == null)
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.NoActiveRequest,
                responsePath);
        }

        if (!string.Equals(
                request.Action,
                LiveInteractionProtocol.LiveAction,
                StringComparison.Ordinal))
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ActiveRequestIsNotFrame,
                responsePath,
                request);
        }

        await _simulationGate.WaitAsync(cancellationToken);
        TemporaryResponseImage? generatedImage = null;
        try
        {
            // The live frame loop posts request N+1 only after it has decoded response N. This is
            // therefore the safe point to remove N's PNG while retaining at most two files during
            // generation/publication of the replacement.
            if (_retainedSimulationCorrelationId != request.CorrelationId)
            {
                ReleaseRetainedSimulationImage();
            }

            cancellationToken.ThrowIfCancellationRequested();
            generatedImage = await Task.Run(
                () => _temporaryResponseImages.CreateTemporaryImage(),
                cancellationToken);
            var result = await _responseSimulator.TryPublishFrameResponseAsync(
                request,
                generatedImage.Path,
                cancellationToken);
            if (!result.IsPublished)
            {
                _temporaryResponseImages.TryReleaseTemporaryImage(generatedImage.Path);
                return result;
            }

            _retainedSimulationImagePath = generatedImage.Path;
            _retainedSimulationCorrelationId = request.CorrelationId;
            GeneratedTestFrameCount++;
            return result;
        }
        catch
        {
            if (generatedImage != null
                && !string.Equals(
                    generatedImage.Path,
                    _retainedSimulationImagePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _temporaryResponseImages.TryReleaseTemporaryImage(generatedImage.Path);
            }

            throw;
        }
        finally
        {
            _simulationGate.Release();
        }
    }

    private void ReportFrameSimulationResult(FrameResponseSimulationResult result)
    {
        switch (result.Status)
        {
            case FrameResponseSimulationStatus.Published:
                SetStatus(
                    "LiveStatusTestFrameResponseGenerated",
                    result.ActiveRequest?.CorrelationId ?? 0);
                break;
            case FrameResponseSimulationStatus.NoActiveRequest:
                SetStatusWarning("LiveStatusTestFrameRequestNotFound");
                break;
            case FrameResponseSimulationStatus.ActiveRequestIsNotLive:
                SetStatusWarning(
                    "LiveStatusTestFrameRequestIsDifferentCommand",
                    result.ActiveRequest?.Action ?? "-");
                break;
            case FrameResponseSimulationStatus.ActiveRequestChanged:
                SetStatusWarning("LiveStatusTestFrameRequestChanged");
                break;
            case FrameResponseSimulationStatus.ResponseAlreadyExists:
                SetStatusWarning(
                    "LiveStatusTestFrameResponseAlreadyExists",
                    result.ActiveRequest?.CorrelationId ?? 0);
                break;
        }
    }

    private void ReleaseRetainedSimulationImage()
    {
        var path = _retainedSimulationImagePath;
        _retainedSimulationImagePath = null;
        _retainedSimulationCorrelationId = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _temporaryResponseImages.TryReleaseTemporaryImage(path!);
        }
    }

    private bool CanExecuteStageMove() =>
        CanExecuteManualEquipmentAction()
        && string.IsNullOrEmpty(StageMoveValidationMessage);

    private async Task ExecuteStageMoveAsync()
    {
        if (!CanExecuteStageMove()
            || !TryParseFiniteInvariant(StageInputXText, out var x)
            || !TryParseFiniteInvariant(StageInputYText, out var y))
        {
            ValidateStageMove();
            return;
        }

        await ExecuteManualEquipmentActionAsync(
            "LiveStatusStageMoving",
            new object[] { StageMoveMode, x, y },
            "LiveStatusStageMoveCompleted",
            "LiveStatusStageMoveFailed",
            token => _session.MoveStageAsync(StageMoveMode, x, y, token),
            response =>
            {
                ApplyStageResponse(response);
                return new object[] { _stageX, _stageY };
            });
    }

    private bool CanExecuteCameraMove() =>
        CanExecuteManualEquipmentAction()
        && string.IsNullOrEmpty(CameraMoveValidationMessage);

    private async Task ExecuteCameraMoveAsync()
    {
        if (!CanExecuteCameraMove()
            || !TryParseFiniteInvariant(CameraInputXText, out var x)
            || !TryParseFiniteInvariant(CameraInputYText, out var y))
        {
            ValidateCameraMove();
            return;
        }

        await ExecuteManualEquipmentActionAsync(
            "LiveStatusCameraMoving",
            new object[] { CameraMoveMode, x, y },
            "LiveStatusCameraMoveCompleted",
            "LiveStatusCameraMoveFailed",
            token => _session.MoveCameraAsync(CameraMoveMode, x, y, token),
            response =>
            {
                ApplyCameraResponse(response);
                return new object[] { _cameraX, _cameraY };
            });
    }

    private bool CanExecuteFocus() =>
        CanExecuteManualEquipmentAction()
        && string.IsNullOrEmpty(FocusValidationMessage);

    private async Task ExecuteFocusAsync()
    {
        if (!CanExecuteFocus()
            || !TryParseFiniteInvariant(FocusHfwText, out var hfw)
            || !TryParseFiniteInvariant(FocusRangeText, out var range)
            || !int.TryParse(
                FocusStepsText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var steps))
        {
            ValidateFocus();
            return;
        }

        await ExecuteManualEquipmentActionAsync(
            "LiveStatusFocusing",
            Array.Empty<object>(),
            "LiveStatusFocusCompleted",
            "LiveStatusFocusFailed",
            token => _session.FocusAsync(hfw, range, steps, token),
            response =>
            {
                ApplyCorrelationResponse(response);
                var sampleCount = response.ZToSharpness2D?.Count ?? 0;
                FocusResultText = FormatLocalized(
                    "LiveFocusResultSamples",
                    new object[] { sampleCount });
                return new object[] { sampleCount };
            });
    }

    private bool CanExecuteManualEquipmentAction() =>
        _isPageActive && !_isExclusiveOperation && !IsWorkflowBusy();

    private async Task ExecuteManualEquipmentActionAsync(
        string startingStatusKey,
        object[] startingStatusArguments,
        string completedStatusKey,
        string failedStatusKey,
        Func<CancellationToken, Task<EquipmentResponseMessage>> exchange,
        Func<EquipmentResponseMessage, object[]> applyResponse)
    {
        var activationGeneration = _activationGeneration;
        var completed = false;
        var resumeStreaming = false;
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
        _moveOperationCancellation = operationCancellation;
        SetExclusiveOperation(true);
        RefreshCommands();
        try
        {
            resumeStreaming = await PauseStreamingForExclusiveOperationAsync();
            if (!CanPublishExclusiveCommand(activationGeneration))
            {
                return;
            }

            IsMoving = true;
            SetStatus(startingStatusKey, startingStatusArguments);
            var response = await exchange(operationCancellation.Token);
            if (_isShuttingDown
                || !_isPageActive
                || activationGeneration != _activationGeneration)
            {
                return;
            }

            var statusArguments = applyResponse(response);
            SetStatus(completedStatusKey, statusArguments);
            completed = true;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "A manual Live equipment action failed.");
            SetStatusError(failedStatusKey, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_moveOperationCancellation, operationCancellation))
            {
                _moveOperationCancellation = null;
            }

            operationCancellation.Dispose();
            IsMoving = false;
            SetExclusiveOperation(false);
            RefreshCommands();
            if (completed)
            {
                ResumeStreamingAfterExclusiveOperation(resumeStreaming);
            }
            else
            {
                SetRestartWhenStreamStops(false);
            }
        }
    }

    private bool CanCapture() =>
        _isPageActive
        && !_isExclusiveOperation
        && !IsWorkflowBusy()
        && string.IsNullOrEmpty(HorizontalFieldWidthValidationMessage)
        && LiveInteractionProtocol.IsValidIntegrationFrameCount(IntegrationFrameCount);

    private async Task CaptureAsync()
    {
        if (!CanCapture())
        {
            return;
        }

        var activationGeneration = _activationGeneration;
        var completed = false;
        var resumeStreaming = false;
        var capturePostResponse = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
        _capturePostResponseCancellation = capturePostResponse;
        LiveImageExchangeResult? imageExchange = null;
        SetExclusiveOperation(true);
        RefreshCommands();
        try
        {
            resumeStreaming = await PauseStreamingForExclusiveOperationAsync();
            if (!CanPublishExclusiveCommand(activationGeneration))
            {
                return;
            }

            IsCapturing = true;
            SetStatus("LiveStatusCapturing");
            imageExchange = await _session.IntegrateAsync(
                _horizontalFieldWidthMetres,
                IntegrationFrameCount,
                capturePostResponse.Token);
            var response = imageExchange.Response;
            if (_isShuttingDown)
            {
                return;
            }

            ApplyCorrelationResponse(response);
            if (!_isPageActive || activationGeneration != _activationGeneration)
            {
                return;
            }

            var sourcePath = response.ImagePath!;
            LiveCaptureLoadResult capture;
            using (var imageIoTimeout = LiveImageIoTimeout.CreateSource(
                       _communicationOptions.ResponseTimeout))
            using (var acquisitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                       capturePostResponse.Token,
                       imageIoTimeout.Token))
            {
                try
                {
                    capture = await LiveImageFileLoader.AcquireCaptureAsync(
                        sourcePath,
                        _captureSnapshots,
                        _imageDecoder,
                        acquisitionCancellation.Token);
                }
                catch (OperationCanceledException exception) when (
                    LiveImageIoTimeout.IsTimeout(
                        imageIoTimeout,
                        capturePostResponse.Token))
                {
                    throw LiveImageIoTimeout.CreateException(
                        _communicationOptions.ResponseTimeout,
                        exception);
                }
            }

            using (capture.Snapshot)
            {
                ApplyImageResponse(response, capture.Image);
                if (!_isPageActive)
                {
                    return;
                }

                var expectedExtension = Path.GetExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(expectedExtension))
                {
                    expectedExtension = capture.Image.DetectedFileExtension;
                }

                var destinationPath = _fileDialogs.ShowSaveImageDialog(sourcePath, expectedExtension);
                if (string.IsNullOrWhiteSpace(destinationPath))
                {
                    SetStatus("LiveStatusCaptureNotSaved");
                    completed = true;
                    return;
                }

                var confirmedDestination = destinationPath!;
                if (!IsLocalDrivePath(confirmedDestination))
                {
                    SetStatusError("LiveStatusLocalPathRequired");
                    return;
                }

                var destinationExtension = Path.GetExtension(confirmedDestination);
                if (!string.Equals(expectedExtension, destinationExtension, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatusError(
                        "LiveStatusImageExtensionMismatch",
                        expectedExtension);
                    return;
                }

                await LiveImageFileLoader.CopyOriginalAsync(
                    capture.Snapshot.Path,
                    confirmedDestination,
                    capturePostResponse.Token);
                SavedImagePath = confirmedDestination;
                SetStatus("LiveStatusCaptureSaved", confirmedDestination);
                completed = true;
            }
        }
        catch (OperationCanceledException) when (capturePostResponse.IsCancellationRequested)
        {
            // Navigation/shutdown can cancel either the owned capture exchange or subsequent
            // image snapshot/decode/save work. The transport removes only this exact request.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "High-quality live capture failed.");
            SetStatusError("LiveStatusCaptureFailed", exception.Message);
        }
        finally
        {
            if (imageExchange is not null)
            {
                // Integration output remains available until the validated snapshot and optional
                // user save have finished. Controller-owned alternate response paths are kept.
                TryDeleteOwnedResponseImage(imageExchange);
            }

            if (ReferenceEquals(_capturePostResponseCancellation, capturePostResponse))
            {
                _capturePostResponseCancellation = null;
            }

            capturePostResponse.Dispose();
            IsCapturing = false;
            SetExclusiveOperation(false);
            RefreshCommands();
            if (completed)
            {
                ResumeStreamingAfterExclusiveOperation(resumeStreaming);
            }
            else
            {
                SetRestartWhenStreamStops(false);
            }
        }
    }

    private bool CanOpenSavedImage() => !string.IsNullOrWhiteSpace(SavedImagePath);

    private void OpenSavedImage()
    {
        var path = SavedImagePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var openedPath = _defaultFileLauncher.Open(path);
            SetStatus("LiveStatusImageOpened", openedPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Opening the saved high-quality capture with its default application failed.");
            SetStatusError("LiveStatusImageOpenFailed", exception.Message);
        }
    }

    private bool CanMoveToTarget(LiveImageTarget? target)
    {
        return target is not null
               && _isPageActive
               && !_isExclusiveOperation
               && !IsWorkflowBusy()
               && HasImage
               && IsDisplayedFrameCalibrationCurrent
               && string.IsNullOrEmpty(PixelPitchValidationMessage);
    }

    private async Task MoveToTargetAsync(LiveImageTarget? target)
    {
        if (target is null || !CanMoveToTarget(target))
        {
            return;
        }

        var moveX = target.MoveXMetres;
        var moveY = target.MoveYMetres;
        SetTarget(target, moveX, moveY);

        var activationGeneration = _activationGeneration;
        var moveCompleted = false;
        var moveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
        _moveOperationCancellation = moveCancellation;
        SetExclusiveOperation(true);
        RefreshCommands();
        try
        {
            await PauseStreamingForExclusiveOperationAsync();
            if (!CanPublishExclusiveCommand(activationGeneration))
            {
                return;
            }

            IsMoving = true;
            SetStatus("LiveStatusMoving", moveX, moveY);
            var response = await _session.MoveStageAsync(
                LiveInteractionProtocol.RelativeMoveMode,
                moveX,
                moveY,
                moveCancellation.Token);
            if (_isShuttingDown
                || !_isPageActive
                || activationGeneration != _activationGeneration)
            {
                return;
            }

            ApplyStageResponse(response);
            IsTargetMarkerVisible = false;
            SetStatus("LiveStatusMoveCompleted", _stageX, _stageY);
            moveCompleted = true;
        }
        catch (OperationCanceledException) when (moveCancellation.IsCancellationRequested)
        {
            // Navigation/shutdown canceled the request owned by this move operation.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Live image target move failed.");
            SetStatusError("LiveStatusMoveFailed", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_moveOperationCancellation, moveCancellation))
            {
                _moveOperationCancellation = null;
            }

            moveCancellation.Dispose();
            IsMoving = false;
            SetExclusiveOperation(false);
            RefreshCommands();
            if (moveCompleted)
            {
                // A deliberate image move always returns to live framing after its matching
                // response, even when the operator initiated it from a manually stopped preview.
                ResumeStreamingAfterExclusiveOperation(resume: true);
            }
            else
            {
                // A failed/canceled move is a decision boundary. Keep framing stopped so the
                // operator can inspect the error instead of immediately issuing another request.
                SetRestartWhenStreamStops(false);
            }
        }
    }

    private async Task<bool> PauseStreamingForExclusiveOperationAsync()
    {
        var resume = IsStreamingRequested;
        IsStreamingRequested = false;
        lock (_streamSync)
        {
            _streamDelayCancellation?.Cancel();
        }

        Task pending;
        lock (_streamSync)
        {
            pending = _streamLoopTask;
        }

        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
            // Stopping the stream now cancels the active frame exchange as well as throttling.
            // The transport performs its ownership-safe request cleanup in the background.
        }

        return resume;
    }

    private void ResumeStreamingAfterExclusiveOperation(bool resume)
    {
        if ((resume || _restartWhenStreamStops)
            && _isPageActive
            && !IsWorkflowBusy()
            && !_isShuttingDown)
        {
            SetRestartWhenStreamStops(false);
            StartStreaming();
        }
    }

    private bool CanPublishExclusiveCommand(int activationGeneration)
    {
        return _isPageActive
               && !_isShuttingDown
               && !IsWorkflowBusy()
               && activationGeneration == _activationGeneration;
    }

    private void ApplyImageResponse(
        EquipmentResponseMessage response,
        LiveImageDecodeResult image,
        double? frameHorizontalFieldWidthMetres = null)
    {
        ApplyCorrelationResponse(response);
        ImagePath = response.ImagePath ?? string.Empty;
        ImagePixelWidth = image.OriginalPixelWidth;
        ImagePixelHeight = image.OriginalPixelHeight;
        ImageDpiX = image.OriginalDpiX;
        ImageDpiY = image.OriginalDpiY;
        LiveImageSource = image.ImageSource;
        IsDisplayedFrameCalibrationCurrent =
            frameHorizontalFieldWidthMetres.HasValue
            && frameHorizontalFieldWidthMetres.Value == _horizontalFieldWidthMetres;
        if (!IsDisplayedFrameCalibrationCurrent)
        {
            IsTargetMarkerVisible = false;
        }
    }

    private void ApplyStageResponse(EquipmentResponseMessage response)
    {
        if (!response.CurrentStageX.HasValue || !response.CurrentStageY.HasValue)
        {
            throw new InvalidOperationException(
                "The stage response must contain current_stage_x and current_stage_y.");
        }

        _stageX = response.CurrentStageX.Value;
        _stageY = response.CurrentStageY.Value;
        ApplyCorrelationResponse(response);
        HasStagePosition = true;
        OnPropertyChanged(nameof(StageXText));
        OnPropertyChanged(nameof(StageYText));
    }

    private void ApplyCameraResponse(EquipmentResponseMessage response)
    {
        if (!response.CurrentCameraX.HasValue || !response.CurrentCameraY.HasValue)
        {
            throw new InvalidOperationException(
                "The camera response must contain current_camera_x and current_camera_y.");
        }

        _cameraX = response.CurrentCameraX.Value;
        _cameraY = response.CurrentCameraY.Value;
        ApplyCorrelationResponse(response);
        HasCameraPosition = true;
        OnPropertyChanged(nameof(CameraXText));
        OnPropertyChanged(nameof(CameraYText));
    }

    private void ApplyCorrelationResponse(EquipmentResponseMessage response)
    {
        LastCorrelationId = response.CorrelationId;
    }

    private void TryDeleteOwnedResponseImage(LiveImageExchangeResult exchange)
    {
        if (!exchange.OwnsResponseImage)
        {
            return;
        }

        var path = exchange.RequestedImagePath;
        _ = Task.Run(() =>
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is NotSupportedException)
            {
                try
                {
                    _logger.LogWarning(
                        exception,
                        "Could not delete app-owned live image {ImagePath}; interaction will continue.",
                        path);
                }
                catch (Exception)
                {
                    // Logging providers can already be disposed during host shutdown.
                }
            }
        });
    }

    private void SetTarget(LiveImageTarget target, double moveX, double moveY)
    {
        TargetPixelX = target.PixelX;
        TargetPixelY = target.PixelY;
        _targetMoveXMetres = moveX;
        _targetMoveYMetres = moveY;
        HasTarget = true;
        IsTargetMarkerVisible = true;
        OnPropertyChanged(nameof(TargetPixelText));
        OnPropertyChanged(nameof(TargetMoveText));
    }

    private bool CanZoomFrameIn()
    {
        return _isPageActive
               && !_isShuttingDown
               && TryParsePositiveLength(
                   _horizontalFieldWidthText,
                   _horizontalFieldWidthUnit,
                   out var metres)
               && LiveInteractionProtocol.IsValidHorizontalFieldWidth(metres / 2d);
    }

    private bool CanZoomFrameOut()
    {
        return _isPageActive
               && !_isShuttingDown
               && TryParsePositiveLength(
                   _horizontalFieldWidthText,
                   _horizontalFieldWidthUnit,
                   out var metres)
               && LiveInteractionProtocol.IsValidHorizontalFieldWidth(metres * 2d);
    }

    private void ZoomFrameIn()
    {
        if (CanZoomFrameIn()
            && TryParsePositiveLength(
                _horizontalFieldWidthText,
                _horizontalFieldWidthUnit,
                out var metres))
        {
            ApplyHorizontalFieldWidth(metres / 2d);
        }
    }

    private void ZoomFrameOut()
    {
        if (CanZoomFrameOut()
            && TryParsePositiveLength(
                _horizontalFieldWidthText,
                _horizontalFieldWidthUnit,
                out var metres))
        {
            ApplyHorizontalFieldWidth(metres * 2d);
        }
    }

    private void ApplyHorizontalFieldWidth(double metres)
    {
        if (!LiveInteractionProtocol.IsValidHorizontalFieldWidth(metres))
        {
            HorizontalFieldWidthValidationMessage =
                _localization["LiveHorizontalFieldWidthInvalid"];
            RefreshCommands();
            return;
        }

        var previousMetres = _horizontalFieldWidthMetres;
        var changed = previousMetres != metres;
        _horizontalFieldWidthMetres = metres;
        _horizontalFieldWidthText = FormatLength(metres, _horizontalFieldWidthUnit);
        HorizontalFieldWidthValidationMessage = string.Empty;
        OnPropertyChanged(nameof(HorizontalFieldWidthMetres));
        OnPropertyChanged(nameof(HorizontalFieldWidthText));
        RefreshCommands();
        if (changed)
        {
            HandleHorizontalFieldWidthChange(previousMetres, metres);
            RestartStreamingForHorizontalFieldWidthChange();
        }
    }

    private void ValidateHorizontalFieldWidth(bool restartStreaming)
    {
        if (TryParsePositiveLength(
                _horizontalFieldWidthText,
                _horizontalFieldWidthUnit,
                out var metres)
            && LiveInteractionProtocol.IsValidHorizontalFieldWidth(metres))
        {
            var previousMetres = _horizontalFieldWidthMetres;
            var changed = previousMetres != metres;
            _horizontalFieldWidthMetres = metres;
            HorizontalFieldWidthValidationMessage = string.Empty;
            OnPropertyChanged(nameof(HorizontalFieldWidthMetres));
            if (changed)
            {
                HandleHorizontalFieldWidthChange(previousMetres, metres);
                if (restartStreaming)
                {
                    RestartStreamingForHorizontalFieldWidthChange();
                }
            }
        }
        else
        {
            HorizontalFieldWidthValidationMessage =
                _localization["LiveHorizontalFieldWidthInvalid"];
        }
    }

    private void HandleHorizontalFieldWidthChange(double previousMetres, double currentMetres)
    {
        if (HasImage)
        {
            IsDisplayedFrameCalibrationCurrent = false;
            IsTargetMarkerVisible = false;
        }

        if (!TryParsePitch(_pixelPitchText, _pixelPitchUnit, out var pitchMetres))
        {
            return;
        }

        var scaledPitch = pitchMetres * (currentMetres / previousMetres);
        if (scaledPitch <= 0d || double.IsNaN(scaledPitch) || double.IsInfinity(scaledPitch))
        {
            _pixelPitchMetres = 0d;
            _pixelPitchText = string.Empty;
            PixelPitchValidationMessage = _localization["LivePixelPitchInvalid"];
        }
        else
        {
            _pixelPitchMetres = scaledPitch;
            _pixelPitchText = FormatPitch(scaledPitch, _pixelPitchUnit);
            PixelPitchValidationMessage = string.Empty;
        }

        OnPropertyChanged(nameof(PixelPitchMetres));
        OnPropertyChanged(nameof(PixelPitchText));
    }

    private void RestartStreamingForHorizontalFieldWidthChange()
    {
        if (!_isPageActive
            || !IsStreamingRequested
            || _isExclusiveOperation
            || IsWorkflowBusy()
            || _isShuttingDown)
        {
            return;
        }

        // An already-published frame still contains the old HFW. Cancel that owned exchange so
        // the transport can remove only its exact payload, then restart with the latest value.
        SetRestartWhenStreamStops(true);
        IsStreamingRequested = false;
        lock (_streamSync)
        {
            _streamDelayCancellation?.Cancel();
        }
    }

    private void ValidatePixelPitch()
    {
        if (TryParsePitch(_pixelPitchText, _pixelPitchUnit, out var metres))
        {
            _pixelPitchMetres = metres;
            PixelPitchValidationMessage = string.Empty;
        }
        else
        {
            PixelPitchValidationMessage = _localization["LivePixelPitchInvalid"];
        }
    }

    private void ValidateStageMove()
    {
        StageMoveValidationMessage =
            LiveInteractionProtocol.IsMoveMode(StageMoveMode)
            && TryParseFiniteInvariant(StageInputXText, out _)
            && TryParseFiniteInvariant(StageInputYText, out _)
                ? string.Empty
                : _localization["LiveStageMoveInvalid"];
        ExecuteStageMoveCommand.NotifyCanExecuteChanged();
    }

    private void ValidateCameraMove()
    {
        CameraMoveValidationMessage =
            LiveInteractionProtocol.IsMoveMode(CameraMoveMode)
            && TryParseFiniteInvariant(CameraInputXText, out _)
            && TryParseFiniteInvariant(CameraInputYText, out _)
                ? string.Empty
                : _localization["LiveCameraMoveInvalid"];
        ExecuteCameraMoveCommand.NotifyCanExecuteChanged();
    }

    private void ValidateFocus()
    {
        var valid = TryParseFiniteInvariant(FocusHfwText, out var hfw)
                    && LiveInteractionProtocol.IsValidHorizontalFieldWidth(hfw)
                    && TryParseFiniteInvariant(FocusRangeText, out var range)
                    && range > 0d
                    && int.TryParse(
                        FocusStepsText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var steps)
                    && steps > 3;
        FocusValidationMessage = valid
            ? string.Empty
            : _localization["LiveFocusInvalid"];
        ExecuteFocusCommand.NotifyCanExecuteChanged();
    }

    private static bool TryParseFiniteInvariant(string raw, out double value)
    {
        var styles = NumberStyles.Float;
        var parsed = double.TryParse(raw, styles, CultureInfo.CurrentCulture, out value)
                     || double.TryParse(raw, styles, CultureInfo.InvariantCulture, out value);
        return parsed && LiveInteractionProtocol.IsFinite(value);
    }

    private static bool TryParsePitch(string raw, string unit, out double metres)
    {
        return TryParsePositiveLength(raw, unit, out metres);
    }

    private static bool TryParsePositiveLength(string raw, string unit, out double metres)
    {
        var styles = NumberStyles.Float;
        var parsed = double.TryParse(raw, styles, CultureInfo.CurrentCulture, out var value)
                     || double.TryParse(raw, styles, CultureInfo.InvariantCulture, out value);
        metres = parsed ? value * UnitScale(unit) : 0d;
        return parsed
               && value > 0d
               && metres > 0d
               && !double.IsNaN(metres)
               && !double.IsInfinity(metres);
    }

    private static string FormatPitch(double metres, string unit)
    {
        return FormatLength(metres, unit);
    }

    private static string FormatLength(double metres, string unit)
    {
        return (metres / UnitScale(unit)).ToString("G12", CultureInfo.CurrentCulture);
    }

    private static double UnitScale(string unit) => unit switch
    {
        "mm" => 1E-3,
        "um" => 1E-6,
        "nm" => 1E-9,
        _ => 1d
    };

    private static string NormalizeUnit(string? unit) => unit switch
    {
        "mm" => "mm",
        "um" => "um",
        "nm" => "nm",
        _ => "m"
    };

    private static bool IsLocalDrivePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)
                || path.Length < 3
                || !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                || path[1] != ':'
                || (path[2] != '\\' && path[2] != '/'))
            {
                return false;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var driveType = new DriveInfo(root).DriveType;
            return driveType == DriveType.Fixed
                   || driveType == DriveType.Removable
                   || driveType == DriveType.Ram;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            || exception is IOException
            || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetExclusiveOperation(bool value)
    {
        if (_isExclusiveOperation == value)
        {
            return;
        }

        _isExclusiveOperation = value;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsInteractionActive));
    }

    private void SetRestartWhenStreamStops(bool value)
    {
        if (_restartWhenStreamStops == value)
        {
            return;
        }

        _restartWhenStreamStops = value;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsInteractionActive));
        StopCommand.NotifyCanExecuteChanged();
    }

    private bool IsWorkflowBusy()
    {
        return _workflowExecution.State is WorkflowRunState.Validating
            or WorkflowRunState.Running
            or WorkflowRunState.Paused
            or WorkflowRunState.Stopping;
    }

    private void SetStatus(string key, params object[] arguments) => SetStatusCore(key, false, false, arguments);

    private void SetStatusWarning(string key, params object[] arguments) => SetStatusCore(key, false, true, arguments);

    private void SetStatusError(string key, params object[] arguments) => SetStatusCore(key, true, false, arguments);

    private void SetStatusCore(string key, bool isError, bool isWarning, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments ?? Array.Empty<object>();
        StatusIsError = isError;
        StatusIsWarning = isWarning;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private string FormatLocalized(string key, object[] arguments)
    {
        var format = _localization[key];
        return arguments.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CaptureCommand.NotifyCanExecuteChanged();
        MoveToTargetCommand.NotifyCanExecuteChanged();
        ExecuteStageMoveCommand.NotifyCanExecuteChanged();
        ExecuteCameraMoveCommand.NotifyCanExecuteChanged();
        ExecuteFocusCommand.NotifyCanExecuteChanged();
        OpenExchangeFolderCommand.NotifyCanExecuteChanged();
        GenerateSingleFrameResponseCommand.NotifyCanExecuteChanged();
        ZoomFrameInCommand.NotifyCanExecuteChanged();
        ZoomFrameOutCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsLive));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ValidateHorizontalFieldWidth(restartStreaming: false);
        ValidatePixelPitch();
        ValidateStageMove();
        ValidateCameraMove();
        ValidateFocus();
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void OnSessionBusyChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshCommands();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(RefreshCommands));
        }
    }

    private void OnWorkflowRunStateChanged(object? sender, WorkflowRunStateChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => OnWorkflowRunStateChanged(sender, e)));
            return;
        }

        if (IsWorkflowBusy())
        {
            _resumeAfterWorkflow = _isPageActive && IsStreamingRequested;
            if (IsStreamingRequested)
            {
                StopStreaming();
            }

            SetStatusWarning("LiveStatusWorkflowBusy");
        }
        else if (_resumeAfterWorkflow && _isPageActive && !_isExclusiveOperation)
        {
            if (IsStreaming)
            {
                SetRestartWhenStreamStops(true);
            }
            else
            {
                _resumeAfterWorkflow = false;
                StartStreaming();
            }
        }

        RefreshCommands();
    }
}

internal static class LiveInteractionCancellation
{
    public static CancellationTokenSource CreatePostResponseSource(
        CancellationToken streamLifecycle,
        CancellationToken applicationShutdown)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            streamLifecycle,
            applicationShutdown);
    }
}

internal static class LiveInteractionShutdownDrain
{
    public static async Task<bool> WaitForCompletionAsync(Task operation, TimeSpan timeout)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (operation.IsCompleted)
        {
            return true;
        }

        using (var timeoutCancellation = new CancellationTokenSource())
        {
            var timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
            var winner = await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false);
            if (ReferenceEquals(winner, operation))
            {
                timeoutCancellation.Cancel();
                return true;
            }

            return false;
        }
    }
}

internal static class LiveImageIoTimeout
{
    private static readonly TimeSpan MinimumBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumCancellationTokenBudget
        = TimeSpan.FromMilliseconds(int.MaxValue);

    public static TimeSpan NormalizeBudget(TimeSpan configured)
    {
        if (configured < MinimumBudget)
        {
            return MinimumBudget;
        }

        return configured > MaximumCancellationTokenBudget
            ? MaximumCancellationTokenBudget
            : configured;
    }

    public static CancellationTokenSource CreateSource(TimeSpan configured)
    {
        return new CancellationTokenSource(NormalizeBudget(configured));
    }

    public static bool IsTimeout(
        CancellationTokenSource timeoutSource,
        CancellationToken lifecycleToken)
    {
        return timeoutSource.IsCancellationRequested
               && !lifecycleToken.IsCancellationRequested;
    }

    public static TimeoutException CreateException(
        TimeSpan configured,
        OperationCanceledException innerException)
    {
        var budget = NormalizeBudget(configured);
        return new TimeoutException(
            $"Image file acquisition exceeded its {budget.TotalMilliseconds:0} ms safety timeout.",
            innerException);
    }
}

internal sealed class LiveCaptureLoadResult
{
    public LiveCaptureLoadResult(
        LiveCaptureSnapshot snapshot,
        LiveImageDecodeResult image)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    public LiveCaptureSnapshot Snapshot { get; }

    public LiveImageDecodeResult Image { get; }
}

internal static class LiveImageFileLoader
{
    private const int MoveFileReplaceExisting = 0x1;
    private const int MoveFileWriteThrough = 0x8;
    private const int LoadAttemptCount = 5;
    private const int CaptureSnapshotAttemptCount = 3;
    private const int RetryDelayMilliseconds = 75;

    public static async Task<LiveImageDecodeResult> LoadAsync(
        string imagePath,
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        if (imageDecoder is null)
        {
            throw new ArgumentNullException(nameof(imageDecoder));
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < LoadAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await LoadOnceAsync(imagePath, imageDecoder, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableImageException(exception))
            {
                // A producer can expose the path before its final write/rename has completed.
                // Retrying both the read and WIC decode avoids accepting a stable-looking but
                // temporarily truncated file.
                lastError = exception;
            }

            if (attempt + 1 < LoadAttemptCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("The live image could not be loaded after bounded retries.", lastError);
    }

    public static async Task<LiveCaptureLoadResult> AcquireCaptureAsync(
        string sourceImagePath,
        ILiveCaptureSnapshotStore snapshotStore,
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        if (snapshotStore is null)
        {
            throw new ArgumentNullException(nameof(snapshotStore));
        }

        if (imageDecoder is null)
        {
            throw new ArgumentNullException(nameof(imageDecoder));
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < CaptureSnapshotAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LiveCaptureSnapshot? snapshot = null;
            try
            {
                snapshot = await snapshotStore
                    .AcquireAsync(sourceImagePath, cancellationToken)
                    .ConfigureAwait(false);
                var image = await LoadOnceAsync(
                        snapshot.Path,
                        imageDecoder,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new LiveCaptureLoadResult(snapshot, image);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                snapshot?.Dispose();
                throw;
            }
            catch (Exception exception) when (IsRetryableImageException(exception))
            {
                snapshot?.Dispose();
                lastError = exception;
            }
            catch
            {
                snapshot?.Dispose();
                throw;
            }

            if (attempt + 1 < CaptureSnapshotAttemptCount)
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            "The equipment capture could not be secured and decoded after bounded retries.",
            lastError);
    }

    public static async Task CopyOriginalAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var normalizedSource = Path.GetFullPath(sourcePath);
        var normalizedDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var destinationDirectory = Path.GetDirectoryName(normalizedDestination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException("The capture destination has no parent directory.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            "." + Path.GetFileName(normalizedDestination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = OpenSharedRead(normalizedSource))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(normalizedDestination))
            {
                if (!MoveFileEx(
                        temporaryPath,
                        normalizedDestination,
                        MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not safely replace the existing captured image.");
                }
            }
            else
            {
                File.Move(temporaryPath, normalizedDestination);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort cleanup only; the original capture remains untouched.
        }
    }

    private static async Task<LiveImageDecodeResult> LoadOnceAsync(
        string imagePath,
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadImageBytesOffUiAsync(imagePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await imageDecoder.DecodeAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadImageBytesOffUiAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var readTask = Task.Run(
            () => ReadImageBytesOnce(imagePath, cancellationToken),
            CancellationToken.None);
        if (!cancellationToken.CanBeCanceled)
        {
            return await readTask.ConfigureAwait(false);
        }

        var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(readTask, cancellationTask).ConfigureAwait(false) == readTask)
        {
            return await readTask.ConfigureAwait(false);
        }

        _ = ObserveAbandonedReadAsync(readTask);
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task ObserveAbandonedReadAsync(Task<byte[]> readTask)
    {
        try
        {
            await readTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller already observed cancellation; avoid an unobserved worker exception.
        }
    }

    private static byte[] ReadImageBytesOnce(string imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new InvalidOperationException("The equipment did not return an image path.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = NormalizePath(imagePath);
        var before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists || before.Length <= 0)
        {
            throw new IOException("The live image does not exist or is empty.");
        }

        var initialLength = before.Length;
        var initialWriteTimeUtc = before.LastWriteTimeUtc;
        LiveImageSafetyLimits.ValidateEncodedByteLength(initialLength);
        var bytes = new byte[(int)initialLength];
        var copiedLength = 0;
        using (var source = OpenSharedRead(path))
        {
            while (copiedLength < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(
                    bytes,
                    copiedLength,
                    bytes.Length - copiedLength);
                if (read == 0)
                {
                    break;
                }

                copiedLength += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (source.ReadByte() >= 0)
            {
                throw new IOException("The live image grew while it was being read.");
            }
        }

        var after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists
            || !LiveCaptureSnapshotStore.IsConsistentSnapshot(
                initialLength,
                initialWriteTimeUtc,
                after.Length,
                after.LastWriteTimeUtc,
                copiedLength))
        {
            throw new IOException("The live image changed while it was being read.");
        }

        return bytes;
    }

    private static bool IsRetryableImageException(Exception exception)
    {
        if (exception is ObjectDisposedException)
        {
            return false;
        }

        return exception is IOException
               || exception is UnauthorizedAccessException
               || exception is System.Security.SecurityException
               || exception is ArgumentException
               || exception is FormatException
               || exception is NotSupportedException
               || exception is InvalidOperationException
               || exception is COMException;
    }

    private static string NormalizePath(string imagePath)
    {
        var trimmed = imagePath.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : trimmed;
    }

    private static FileStream OpenSharedRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        int flags);
}
