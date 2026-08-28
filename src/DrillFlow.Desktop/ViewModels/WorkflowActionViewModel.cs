using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Application.Execution;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.ViewModels;

public sealed class WorkflowActionViewModel : ObservableObject
{
    internal const double MinimumResultImageZoom = 0.5;
    internal const double MaximumResultImageZoom = 3.0;
    internal const double ResultImageZoomStep = 0.25;

    private static readonly Regex AliasPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private readonly ILocalizationService _localization;
    private readonly ILiveImageDecoder _imageDecoder;
    private readonly object _imageLoadSync = new();
    private bool _isSelected;
    private bool _isCurrent;
    private bool _isEditingEnabled = true;
    private string _aliasValidationMessage = string.Empty;
    private string _validationErrorText = string.Empty;
    private WorkflowNodeExecutionState _runtimeState = WorkflowNodeExecutionState.Waiting;
    private CancellationTokenSource? _latestImageLoadCancellation;
    private long _latestImageLoadGeneration;
    private ImageSource? _latestImageSource;
    private bool _isLatestImageLoading;
    private bool _hasLatestImageLoadError;
    private bool _isResultExpanded = true;
    private double _resultImageZoom = 1.0;
    private bool _suppressLatestImageRefresh;

    public WorkflowActionViewModel(
        WorkflowNode model,
        ILocalizationService localization,
        ILiveImageDecoder imageDecoder)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _imageDecoder = imageDecoder ?? throw new ArgumentNullException(nameof(imageDecoder));
        Parameters = new ObservableCollection<ActionParameterViewModel>();
        Children = new ObservableCollection<WorkflowActionViewModel>();
        Branches = new ObservableCollection<WorkflowBranchViewModel>();
        Results = new ObservableCollection<RuntimeResultViewModel>();
        Results.CollectionChanged += OnResultsChanged;

        foreach (var pair in model.GetParameterBindings())
        {
            Parameters.Add(new ActionParameterViewModel(
                pair.Key,
                GetParameterLabelKey(pair.Key),
                pair.Value,
                model.Kind,
                localization));
        }

        if (model is RepeatNode repeat)
        {
            foreach (var child in repeat.Body)
            {
                Children.Add(new WorkflowActionViewModel(child, localization, imageDecoder));
            }

            Children.CollectionChanged += OnRepeatChildrenChanged;
        }
        else if (model is ConditionalNode conditional)
        {
            foreach (var branch in conditional.Branches)
            {
                Branches.Add(new WorkflowBranchViewModel(branch, localization, imageDecoder));
            }

            Branches.CollectionChanged += OnBranchesChanged;
        }

        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(RuntimeStateText));
            OnPropertyChanged(nameof(RunningStatusText));
            OnPropertyChanged(nameof(ParameterObjectLabel));
            OnPropertyChanged(nameof(ResultObjectLabel));
            OnPropertyChanged(nameof(LatestImageStatusText));
            foreach (var result in Results)
            {
                result.NotifyLanguageChanged();
            }
        };
    }

    public WorkflowNode Model { get; }

    public Guid Id => Model.Id;

    public WorkflowNodeKind Kind => Model.Kind;

    public string Title => _localization[Model.Kind switch
    {
        WorkflowNodeKind.Stage => "ActionStage",
        WorkflowNodeKind.Camera => "ActionCamera",
        WorkflowNodeKind.Focus => "ActionFocus",
        WorkflowNodeKind.Integration => "ActionIntegration",
        WorkflowNodeKind.Live => "ActionLive",
        WorkflowNodeKind.Om => "ActionOm",
        WorkflowNodeKind.Lens => "ActionLens",
        WorkflowNodeKind.AutoContrastBrightness => "ActionAcb",
        WorkflowNodeKind.Abort => "ActionAbort",
        WorkflowNodeKind.Http => "ActionHttp",
        WorkflowNodeKind.Delay => "ActionDelay",
        WorkflowNodeKind.Repeat => "ActionRepeat",
        _ => "ActionConditional"
    }];

    public SymbolRegular Icon => Model.Kind switch
    {
        WorkflowNodeKind.Stage => SymbolRegular.ArrowMove20,
        WorkflowNodeKind.Camera => SymbolRegular.Camera20,
        WorkflowNodeKind.Focus => SymbolRegular.ScanCamera20,
        WorkflowNodeKind.Integration => SymbolRegular.ImageMultiple20,
        WorkflowNodeKind.Live => SymbolRegular.Live20,
        WorkflowNodeKind.Om => SymbolRegular.Microscope20,
        WorkflowNodeKind.Lens => SymbolRegular.CameraSwitch20,
        WorkflowNodeKind.AutoContrastBrightness => SymbolRegular.BrightnessHigh20,
        WorkflowNodeKind.Abort => SymbolRegular.Stop20,
        WorkflowNodeKind.Http => SymbolRegular.Globe20,
        WorkflowNodeKind.Delay => SymbolRegular.Timer20,
        WorkflowNodeKind.Repeat => SymbolRegular.ArrowRepeatAll20,
        _ => SymbolRegular.BranchCompare20
    };

    public string Alias
    {
        get => Model.Key;
        set
        {
            if (!IsEditingEnabled)
            {
                return;
            }

            if (string.Equals(Model.Key, value, StringComparison.Ordinal))
            {
                return;
            }

            Model.Key = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParameterObjectLabel));
            OnPropertyChanged(nameof(ResultObjectLabel));
            foreach (var result in Results)
            {
                result.UpdateActionAlias(Model.Key);
            }

            ValidateAlias();
        }
    }

    public string ParameterObjectLabel =>
        Alias + ".parameters (" + _localization["Parameters"] + ")";

    public string ResultObjectLabel =>
        Alias + ".result / " + Alias + ".results / " + Alias + ".last ("
        + _localization["Results"] + ")";

    public bool HasBreakpoint
    {
        get => Model.HasBreakpoint;
        set
        {
            if (!IsEditingEnabled)
            {
                return;
            }

            if (Model.HasBreakpoint == value)
            {
                return;
            }

            Model.HasBreakpoint = value;
            OnPropertyChanged();
        }
    }

    public bool IsNodeEnabled
    {
        get => Model.IsEnabled;
        set
        {
            if (!IsEditingEnabled || Model.IsEnabled == value)
            {
                return;
            }

            Model.IsEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsEditingEnabled
    {
        get => _isEditingEnabled;
        private set => SetProperty(ref _isEditingEnabled, value);
    }

    public WorkflowNodeExecutionState RuntimeState
    {
        get => _runtimeState;
        set
        {
            if (SetProperty(ref _runtimeState, value))
            {
                OnPropertyChanged(nameof(RuntimeStateText));
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    public string RuntimeStateText => _localization[RuntimeState switch
    {
        WorkflowNodeExecutionState.Running => "StatusRunning",
        WorkflowNodeExecutionState.Paused => "StatusPaused",
        WorkflowNodeExecutionState.Completed => "StatusCompleted",
        WorkflowNodeExecutionState.Stopped => "StatusStopped",
        WorkflowNodeExecutionState.Faulted => "StatusFaulted",
        WorkflowNodeExecutionState.Skipped => "StatusSkipped",
        _ => "StatusIdle"
    }];

    public bool IsRunning => RuntimeState == WorkflowNodeExecutionState.Running;

    public string RunningStatusText => _localization[Kind is WorkflowNodeKind.Stage
        or WorkflowNodeKind.Camera
        or WorkflowNodeKind.Focus
        or WorkflowNodeKind.Integration
        or WorkflowNodeKind.Live
        or WorkflowNodeKind.Om
        or WorkflowNodeKind.Lens
        or WorkflowNodeKind.AutoContrastBrightness
        or WorkflowNodeKind.Abort
            ? "ActionRunning"
            : "DesignerActionRunning"];

    public ObservableCollection<ActionParameterViewModel> Parameters { get; }

    public ObservableCollection<WorkflowActionViewModel> Children { get; }

    public ObservableCollection<WorkflowBranchViewModel> Branches { get; }

    public ObservableCollection<RuntimeResultViewModel> Results { get; }

    public RuntimeResultViewModel? LatestResult => Results.Count == 0 ? null : Results[Results.Count - 1];

    public bool HasLatestResultSummary => LatestResult?.HasSummaryFields == true;

    public bool HasRuntimeResults => Results.Count > 0;

    public bool IsResultExpanded
    {
        get => _isResultExpanded;
        set => SetProperty(ref _isResultExpanded, value);
    }

    public string LatestImagePath => LatestResult?.ImagePath ?? string.Empty;

    public bool HasLatestImagePath => !string.IsNullOrWhiteSpace(LatestImagePath);

    public ImageSource? LatestImageSource => _latestImageSource;

    public bool HasLatestImage => LatestImageSource is not null;

    public double ResultImageZoom
    {
        get => _resultImageZoom;
        private set
        {
            var normalized = Math.Max(
                MinimumResultImageZoom,
                Math.Min(MaximumResultImageZoom, Math.Round(value, 2)));
            if (SetProperty(ref _resultImageZoom, normalized))
            {
                OnPropertyChanged(nameof(CanZoomResultImageIn));
                OnPropertyChanged(nameof(CanZoomResultImageOut));
                OnPropertyChanged(nameof(ResultImageZoomText));
            }
        }
    }

    public bool CanZoomResultImageIn => HasLatestImage && ResultImageZoom < MaximumResultImageZoom;

    public bool CanZoomResultImageOut => HasLatestImage && ResultImageZoom > MinimumResultImageZoom;

    public string ResultImageZoomText => ResultImageZoom.ToString("P0");

    public bool IsLatestImageLoading => _isLatestImageLoading;

    public bool HasLatestImageLoadError => _hasLatestImageLoadError;

    public string LatestImageStatusText => HasLatestImagePath
        ? _localization[HasLatestImageLoadError ? "ResultImageLoadFailed" : "ResultImageLoading"]
        : _localization["NoResultImage"];

    public bool HasChildrenContainer => Model is RepeatNode;

    public bool HasBranches => Model is ConditionalNode;

    public string AliasValidationMessage
    {
        get => _aliasValidationMessage;
        private set
        {
            if (SetProperty(ref _aliasValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasAliasError));
            }
        }
    }

    public bool HasAliasError => !string.IsNullOrEmpty(AliasValidationMessage);

    public string ValidationErrorText
    {
        get => _validationErrorText;
        private set
        {
            if (SetProperty(ref _validationErrorText, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationErrorText);

    public void SetExternalAliasError(string message)
    {
        AliasValidationMessage = message ?? string.Empty;
    }

    public void SetValidationErrors(IEnumerable<string> messages)
    {
        ValidationErrorText = string.Join(
            Environment.NewLine,
            (messages ?? Enumerable.Empty<string>())
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim())
                .Distinct(StringComparer.Ordinal));
    }

    public bool Validate()
    {
        var valid = ValidateAlias();
        foreach (var parameter in Parameters)
        {
            valid &= parameter.Validate();
        }

        foreach (var child in Children)
        {
            valid &= child.Validate();
        }

        foreach (var branch in Branches)
        {
            valid &= branch.Validate();
        }

        return valid;
    }

    public IEnumerable<WorkflowActionViewModel> EnumerateDepthFirst()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.EnumerateDepthFirst())
            {
                yield return descendant;
            }
        }

        foreach (var branch in Branches)
        {
            foreach (var child in branch.Children)
            {
                foreach (var descendant in child.EnumerateDepthFirst())
                {
                    yield return descendant;
                }
            }
        }
    }

    public void ClearRuntime()
    {
        RuntimeState = WorkflowNodeExecutionState.Waiting;
        IsCurrent = false;
        IsResultExpanded = true;
        ResetResultImageZoom();
        Results.Clear();

        foreach (var child in Children)
        {
            child.ClearRuntime();
        }

        foreach (var branch in Branches)
        {
            foreach (var child in branch.Children)
            {
                child.ClearRuntime();
            }
        }
    }

    public void AddResult(ActionExecutionResult result)
    {
        ResetResultImageZoom();
        Results.Add(new RuntimeResultViewModel(result, Alias, _localization));
    }

    public void ZoomResultImageIn()
    {
        if (CanZoomResultImageIn)
        {
            ResultImageZoom += ResultImageZoomStep;
        }
    }

    public void ZoomResultImageOut()
    {
        if (CanZoomResultImageOut)
        {
            ResultImageZoom -= ResultImageZoomStep;
        }
    }

    internal void RestoreRuntimeFrom(WorkflowActionViewModel source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        RuntimeState = source.RuntimeState;
        IsCurrent = false;
        IsResultExpanded = source.IsResultExpanded;
        _suppressLatestImageRefresh = true;
        try
        {
            foreach (var result in source.Results)
            {
                result.UpdateActionAlias(Alias);
                Results.Add(result);
            }
        }
        finally
        {
            _suppressLatestImageRefresh = false;
        }

        ResultImageZoom = source.ResultImageZoom;
        if (source.LatestImageSource is not null)
        {
            ReplaceLatestImageState(
                source.LatestImageSource,
                isLoading: false,
                hasLoadError: false);
        }
        else
        {
            RefreshLatestImage();
        }
    }

    public void SetEditingEnabled(bool enabled)
    {
        IsEditingEnabled = enabled;
        foreach (var parameter in Parameters)
        {
            parameter.SetEditingEnabled(enabled);
        }

        foreach (var child in Children)
        {
            child.SetEditingEnabled(enabled);
        }

        foreach (var branch in Branches)
        {
            branch.SetEditingEnabled(enabled);
        }
    }

    private bool ValidateAlias()
    {
        AliasValidationMessage = AliasPattern.IsMatch(Alias ?? string.Empty)
            ? string.Empty
            : string.Equals(_localization.EffectiveLanguage, "en-US", StringComparison.OrdinalIgnoreCase)
                ? "Use letters, numbers, and underscores; start with a letter or underscore."
                : "영문자, 숫자, 밑줄을 사용하고 영문자 또는 밑줄로 시작하세요.";
        return !HasAliasError;
    }

    private void OnRepeatChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Model is not RepeatNode repeat)
        {
            return;
        }

        repeat.Body.Clear();
        repeat.Body.AddRange(Children.Select(child => child.Model));
    }

    private void OnBranchesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Model is not ConditionalNode conditional)
        {
            return;
        }

        conditional.Branches.Clear();
        conditional.Branches.AddRange(Branches.Select(branch => branch.Model));
    }

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressLatestImageRefresh)
        {
            RefreshLatestImage();
        }
        OnPropertyChanged(nameof(LatestResult));
        OnPropertyChanged(nameof(HasLatestResultSummary));
        OnPropertyChanged(nameof(HasRuntimeResults));
        OnPropertyChanged(nameof(LatestImagePath));
        OnPropertyChanged(nameof(HasLatestImagePath));
        OnPropertyChanged(nameof(LatestImageStatusText));
    }

    private void RefreshLatestImage()
    {
        var latestResult = LatestResult;
        CancellationTokenSource? cancellation = null;
        long generation;

        lock (_imageLoadSync)
        {
            _latestImageLoadCancellation?.Cancel();
            _latestImageLoadCancellation = null;
            generation = ++_latestImageLoadGeneration;
            if (!string.IsNullOrWhiteSpace(latestResult?.ImagePath))
            {
                cancellation = new CancellationTokenSource();
                _latestImageLoadCancellation = cancellation;
            }
        }

        PublishLatestImageState(
            image: null,
            isLoading: cancellation is not null,
            hasLoadError: false,
            generation: generation);
        if (cancellation is not null)
        {
            _ = LoadLatestImageAsync(latestResult!, cancellation, generation);
        }
    }

    private void ResetResultImageZoom()
    {
        ResultImageZoom = 1.0;
    }

    private void ReplaceLatestImageState(
        ImageSource? image,
        bool isLoading,
        bool hasLoadError)
    {
        long generation;
        lock (_imageLoadSync)
        {
            _latestImageLoadCancellation?.Cancel();
            _latestImageLoadCancellation = null;
            generation = ++_latestImageLoadGeneration;
        }

        PublishLatestImageState(image, isLoading, hasLoadError, generation);
    }

    private async Task LoadLatestImageAsync(
        RuntimeResultViewModel result,
        CancellationTokenSource cancellation,
        long generation)
    {
        try
        {
            var image = await result
                .LoadImageAsync(_imageDecoder, cancellation.Token)
                .ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested)
            {
                PublishLatestImageState(
                    image,
                    isLoading: false,
                    hasLoadError: image is null,
                    generation: generation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Image failures do not change the action result or interrupt the workflow UI.
            PublishLatestImageState(
                image: null,
                isLoading: false,
                hasLoadError: true,
                generation: generation);
        }
        finally
        {
            lock (_imageLoadSync)
            {
                if (ReferenceEquals(_latestImageLoadCancellation, cancellation))
                {
                    _latestImageLoadCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void PublishLatestImageState(
        ImageSource? image,
        bool isLoading,
        bool hasLoadError,
        long generation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyLatestImageState(image, isLoading, hasLoadError, generation);
            return;
        }

        dispatcher.BeginInvoke(
            new Action(() => ApplyLatestImageState(image, isLoading, hasLoadError, generation)),
            DispatcherPriority.DataBind);
    }

    private void ApplyLatestImageState(
        ImageSource? image,
        bool isLoading,
        bool hasLoadError,
        long generation)
    {
        bool imageChanged;
        bool loadingChanged;
        bool errorChanged;
        lock (_imageLoadSync)
        {
            if (generation != _latestImageLoadGeneration)
            {
                return;
            }

            imageChanged = !ReferenceEquals(_latestImageSource, image);
            loadingChanged = _isLatestImageLoading != isLoading;
            errorChanged = _hasLatestImageLoadError != hasLoadError;
            _latestImageSource = image;
            _isLatestImageLoading = isLoading;
            _hasLatestImageLoadError = hasLoadError;
        }

        if (imageChanged)
        {
            OnPropertyChanged(nameof(LatestImageSource));
            OnPropertyChanged(nameof(HasLatestImage));
            OnPropertyChanged(nameof(CanZoomResultImageIn));
            OnPropertyChanged(nameof(CanZoomResultImageOut));
        }

        if (loadingChanged)
        {
            OnPropertyChanged(nameof(IsLatestImageLoading));
        }

        if (errorChanged)
        {
            OnPropertyChanged(nameof(HasLatestImageLoadError));
        }

        if (imageChanged || loadingChanged || errorChanged)
        {
            OnPropertyChanged(nameof(LatestImageStatusText));
        }
    }

    private static string GetParameterLabelKey(string name) => name switch
    {
        "move_mode" => "ParamMoveMode",
        "stage_x" => "ParamStageX",
        "stage_y" => "ParamStageY",
        "camera_x" => "ParamCameraX",
        "camera_y" => "ParamCameraY",
        "hfw" => "ParamHfw",
        "range" => "ParamFocusRange",
        "steps" => "ParamFocusSteps",
        "frame_count" => "ParamFrameCount",
        "image_path" => "ParamImagePath",
        "lens_mode" => "ParamLensMode",
        "method" => "ParamHttpMethod",
        "url" => "ParamHttpUrl",
        "headers" => "ParamHttpHeaders",
        "body" => "ParamHttpBody",
        "timeout_ms" => "ParamHttpTimeout",
        "milliseconds" => "ParamDelay",
        "count" => "ParamCount",
        "condition" => "ParamCondition",
        _ => name
    };
}
