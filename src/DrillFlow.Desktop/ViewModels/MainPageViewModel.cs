using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrillFlow.Application.Execution;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Validation;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.ViewModels;

public sealed class MainPageViewModel : ObservableObject, IExpressionCompletionSource
{
    private readonly ILocalizationService _localization;
    private readonly IWorkflowDocumentService _documentService;
    private readonly IWorkflowExecutionFacade _execution;
    private readonly IFileDialogService _fileDialogs;
    private readonly IResponseSimulationDialogService _responseSimulationDialogs;
    private readonly IExchangeFolderLauncher _exchangeFolderLauncher;
    private readonly WorkflowValidator _workflowValidator;
    private readonly ExpressionCompletionProvider _expressionCompletions = new();
    private readonly ILogger<MainPageViewModel> _logger;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private WorkflowDocument _document = new();
    private WorkflowActionViewModel? _selectedAction;
    private string? _documentPath;
    private bool _isDirty;
    private bool _suppressCollectionSync;
    private WorkflowRunState _runState = WorkflowRunState.Idle;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private TaskCompletionSource<bool>? _terminalStateWaiter;
    private string? _clipboardSnapshot;
    private ObservableCollection<WorkflowActionViewModel>? _pasteDestination;
    private int _pasteIndex;

    public MainPageViewModel(
        ILocalizationService localization,
        IWorkflowDocumentService documentService,
        IWorkflowExecutionFacade execution,
        IFileDialogService fileDialogs,
        IResponseSimulationDialogService responseSimulationDialogs,
        IExchangeFolderLauncher exchangeFolderLauncher,
        WorkflowValidator workflowValidator,
        ILogger<MainPageViewModel> logger)
    {
        _localization = localization;
        _documentService = documentService;
        _execution = execution;
        _fileDialogs = fileDialogs;
        _responseSimulationDialogs = responseSimulationDialogs;
        _exchangeFolderLauncher = exchangeFolderLauncher;
        _workflowValidator = workflowValidator;
        _logger = logger;

        Actions = new ObservableCollection<WorkflowActionViewModel>();
        Actions.CollectionChanged += OnRootActionsChanged;

        EquipmentToolboxItems = new ObservableCollection<ToolboxItemViewModel>
        {
            new(WorkflowNodeKind.Move, "ActionMove", "ToolboxMoveDescription", SymbolRegular.ArrowMove20, localization),
            new(WorkflowNodeKind.Measure, "ActionMeasure", "ToolboxMeasureDescription", SymbolRegular.Ruler20, localization),
            new(WorkflowNodeKind.Drill, "ActionDrill", "ToolboxDrillDescription", SymbolRegular.Toolbox20, localization),
            new(WorkflowNodeKind.Abort, "ActionAbort", "ToolboxAbortDescription", SymbolRegular.Stop20, localization)
        };
        FlowToolboxItems = new ObservableCollection<ToolboxItemViewModel>
        {
            new(WorkflowNodeKind.Http, "ActionHttp", "ToolboxHttpDescription", SymbolRegular.Globe20, localization),
            new(WorkflowNodeKind.Delay, "ActionDelay", "ToolboxDelayDescription", SymbolRegular.Timer20, localization),
            new(WorkflowNodeKind.Repeat, "ActionRepeat", "ToolboxRepeatDescription", SymbolRegular.ArrowRepeatAll20, localization),
            new(WorkflowNodeKind.Conditional, "ActionConditional", "ToolboxConditionalDescription", SymbolRegular.BranchCompare20, localization)
        };

        NewCommand = new AsyncRelayCommand(NewAsync, () => IsWorkflowEditingEnabled);
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => IsWorkflowEditingEnabled);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsWorkflowEditingEnabled);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => IsWorkflowEditingEnabled);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0 && IsWorkflowEditingEnabled);
        RedoCommand = new RelayCommand(Redo, () => _redo.Count > 0 && IsWorkflowEditingEnabled);
        ValidateCommand = new RelayCommand(() => ValidateWorkflow(true), () => !IsExecutionBusy);
        RunCommand = new AsyncRelayCommand(RunAsync, () => Actions.Count > 0 && IsWorkflowEditingEnabled);
        RunSelectedCommand = new AsyncRelayCommand(
            RunSelectedAsync,
            () => SelectedAction is { IsNodeEnabled: true } && IsWorkflowEditingEnabled);
        TestResponseCommand = new AsyncRelayCommand(
            TestResponseAsync,
            () => SelectedAction is not null && IsEquipmentAction(SelectedAction.Kind));
        OpenExchangeFolderCommand = new RelayCommand(OpenExchangeFolder);
        ContinueCommand = new RelayCommand(Continue, () => RunState == WorkflowRunState.Paused);
        StepCommand = new RelayCommand(Step, () => RunState == WorkflowRunState.Paused);
        StopCommand = new RelayCommand(
            Stop,
            () => RunState is WorkflowRunState.Running or WorkflowRunState.Paused or WorkflowRunState.Stopping);
        ToggleBreakpointCommand = new RelayCommand(ToggleBreakpoint, () => SelectedAction is not null && IsWorkflowEditingEnabled);
        ClearBreakpointsCommand = new RelayCommand(ClearBreakpoints, () => EnumerateActions().Any(action => action.HasBreakpoint) && IsWorkflowEditingEnabled);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedAction is not null && IsWorkflowEditingEnabled);
        CopySelectedCommand = new RelayCommand(CopySelected, () => SelectedAction is not null);
        CutSelectedCommand = new RelayCommand(CutSelected, () => SelectedAction is not null && IsWorkflowEditingEnabled);
        PasteCommand = new RelayCommand(Paste, () => _clipboardSnapshot is not null && IsWorkflowEditingEnabled);
        ToggleEnabledCommand = new RelayCommand(
            ToggleEnabled,
            () => SelectedAction is not null && IsWorkflowEditingEnabled);
        AddToolboxItemCommand = new RelayCommand<ToolboxItemViewModel>(AppendToolboxItem, item => item is not null && IsWorkflowEditingEnabled);
        AddElseIfCommand = new RelayCommand(AddElseIf, CanAddElseIf);
        AddElseCommand = new RelayCommand(AddElse, CanAddElse);

        _execution.RunStateChanged += OnRunStateChanged;
        _execution.NodeStateChanged += OnNodeStateChanged;
        _localization.LanguageChanged += OnLanguageChanged;

        ReplaceDocument(new WorkflowDocument { Name = _localization["DocumentUntitled"] }, null, false);
    }

    public ObservableCollection<WorkflowActionViewModel> Actions { get; }

    public ObservableCollection<ToolboxItemViewModel> EquipmentToolboxItems { get; }

    public ObservableCollection<ToolboxItemViewModel> FlowToolboxItems { get; }

    public IAsyncRelayCommand NewCommand { get; }

    public IAsyncRelayCommand OpenCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand SaveAsCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    public IRelayCommand ValidateCommand { get; }

    public IAsyncRelayCommand RunCommand { get; }

    public IAsyncRelayCommand RunSelectedCommand { get; }

    public IAsyncRelayCommand TestResponseCommand { get; }

    public IRelayCommand OpenExchangeFolderCommand { get; }

    public IRelayCommand ContinueCommand { get; }

    public IRelayCommand StepCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IRelayCommand ToggleBreakpointCommand { get; }

    public IRelayCommand ClearBreakpointsCommand { get; }

    public IRelayCommand DeleteSelectedCommand { get; }

    public IRelayCommand CopySelectedCommand { get; }

    public IRelayCommand CutSelectedCommand { get; }

    public IRelayCommand PasteCommand { get; }

    public IRelayCommand ToggleEnabledCommand { get; }

    public IRelayCommand<ToolboxItemViewModel> AddToolboxItemCommand { get; }

    public IRelayCommand AddElseIfCommand { get; }

    public IRelayCommand AddElseCommand { get; }

    public WorkflowActionViewModel? SelectedAction
    {
        get => _selectedAction;
        private set
        {
            if (_selectedAction == value)
            {
                return;
            }

            if (_selectedAction is not null)
            {
                _selectedAction.IsSelected = false;
            }

            _selectedAction = value;
            if (_selectedAction is not null)
            {
                _selectedAction.IsSelected = true;
            }

            OnPropertyChanged();
            NotifyCommandStates();
        }
    }

    public string DocumentName => string.IsNullOrWhiteSpace(_document.Name)
        ? _localization["DocumentUntitled"]
        : _document.Name;

    public string DocumentDisplayName => DocumentName + (IsDirty ? " *" : string.Empty);

    public string? DocumentPath => _documentPath;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DocumentDisplayName));
            }
        }
    }

    public WorkflowRunState RunState
    {
        get => _runState;
        private set
        {
            if (SetProperty(ref _runState, value))
            {
                OnPropertyChanged(nameof(RuntimeStatusText));
                OnPropertyChanged(nameof(IsExecutionBusy));
                OnPropertyChanged(nameof(IsWorkflowEditingEnabled));
                SetWorkflowEditingEnabled(IsWorkflowEditingEnabled);
                NotifyCommandStates();
            }
        }
    }

    public string RuntimeStatusText => _localization[RunState switch
    {
        WorkflowRunState.Validating => "StatusValidating",
        WorkflowRunState.Running => "StatusRunning",
        WorkflowRunState.Paused => "StatusPaused",
        WorkflowRunState.Stopping => "StatusStopping",
        WorkflowRunState.Completed => "StatusCompleted",
        WorkflowRunState.Stopped => "StatusStopped",
        WorkflowRunState.Faulted => "StatusFaulted",
        _ => "StatusIdle"
    }];

    public bool IsExecutionBusy => RunState is WorkflowRunState.Validating
        or WorkflowRunState.Running
        or WorkflowRunState.Paused
        or WorkflowRunState.Stopping;

    public bool IsWorkflowEditingEnabled => !IsExecutionBusy && !IsBusyState(_execution.State);

    public bool CanCompleteExpressions => IsWorkflowEditingEnabled;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public void SelectAction(WorkflowActionViewModel? action) => SelectedAction = action;

    public ExpressionCompletionResult GetExpressionCompletions(
        Guid ownerNodeId,
        string rawText,
        int caretIndex)
    {
        if (!CanCompleteExpressions)
        {
            return ExpressionCompletionResult.Empty(caretIndex);
        }

        var observedResultMembers = new Dictionary<Guid, IReadOnlyCollection<string>>();
        foreach (var node in _document.EnumerateNodesDepthFirst())
        {
            var names = _execution.Results
                .GetAll(node.Id)
                .SelectMany(result => result.Values.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length > 0)
            {
                observedResultMembers[node.Id] = names;
            }
        }

        return _expressionCompletions.GetCompletions(
            _document,
            ownerNodeId,
            rawText,
            caretIndex,
            observedResultMembers);
    }

    public async Task<bool> PrepareForCloseAsync()
    {
        if (IsExecutionBusy)
        {
            _terminalStateWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _execution.RequestStop();
            RunState = WorkflowRunState.Stopping;
            StatusMessage = _localization["StatusStopping"];

            if (!IsTerminalState(_execution.State))
            {
                await _terminalStateWaiter.Task;
            }

            _terminalStateWaiter = null;
        }

        return await ConfirmUnsavedChangesAsync();
    }

    public void CaptureUndoCheckpoint()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        _undo.Push(_documentService.Serialize(_document));
        _redo.Clear();
        NotifyCommandStates();
    }

    public WorkflowActionViewModel? CreateAndInsert(
        WorkflowNodeKind kind,
        ObservableCollection<WorkflowActionViewModel> destination,
        int index)
    {
        if (!IsWorkflowEditingEnabled)
        {
            return null;
        }

        CaptureUndoCheckpoint();
        var node = WorkflowNodeFactory.Create(kind, EnumerateActions().Select(action => action.Alias));
        var viewModel = new WorkflowActionViewModel(node, _localization);
        AttachAction(viewModel);
        destination.Insert(Math.Max(0, Math.Min(index, destination.Count)), viewModel);
        SelectAction(viewModel);
        IsDirty = true;
        return viewModel;
    }

    public bool MoveAction(
        WorkflowActionViewModel action,
        ObservableCollection<WorkflowActionViewModel> destination,
        int index)
    {
        if (!IsWorkflowEditingEnabled || IsCollectionInside(action, destination))
        {
            return false;
        }

        var source = FindCollectionContaining(action);
        if (source is null)
        {
            return false;
        }

        CaptureUndoCheckpoint();
        var sourceIndex = source.IndexOf(action);
        source.Remove(action);
        if (ReferenceEquals(source, destination) && sourceIndex < index)
        {
            index--;
        }

        destination.Insert(Math.Max(0, Math.Min(index, destination.Count)), action);
        SelectAction(action);
        IsDirty = true;
        return true;
    }

    public bool CanMoveAction(
        WorkflowActionViewModel action,
        ObservableCollection<WorkflowActionViewModel> destination)
    {
        return IsWorkflowEditingEnabled
               && FindCollectionContaining(action) is not null
               && !IsCollectionInside(action, destination);
    }

    public ObservableCollection<WorkflowActionViewModel>? FindCollectionContaining(WorkflowActionViewModel action)
    {
        return FindCollectionContaining(Actions, action);
    }

    public bool TryResolveDropTarget(
        object? target,
        out ObservableCollection<WorkflowActionViewModel> destination,
        out int index)
    {
        if (!IsWorkflowEditingEnabled)
        {
            destination = Actions;
            index = Actions.Count;
            return false;
        }

        if (target is ObservableCollection<WorkflowActionViewModel> collection)
        {
            destination = collection;
            index = collection.Count;
            return true;
        }

        if (target is WorkflowActionViewModel action)
        {
            var parent = FindCollectionContaining(action);
            if (parent is not null)
            {
                destination = parent;
                index = parent.IndexOf(action);
                return true;
            }
        }

        destination = Actions;
        index = Actions.Count;
        return target is null;
    }

    public bool SetPasteTarget(object? target)
    {
        if (!TryResolveDropTarget(target, out var destination, out var index))
        {
            return false;
        }

        _pasteDestination = destination;
        _pasteIndex = index;
        NotifyCommandStates();
        return true;
    }

    public bool CopyActionTo(
        WorkflowActionViewModel action,
        ObservableCollection<WorkflowActionViewModel> destination,
        int index)
    {
        if (!IsWorkflowEditingEnabled
            || FindCollectionContaining(action) is null
            || !IsKnownCollection(destination))
        {
            return false;
        }

        CaptureUndoCheckpoint();
        var clonedNode = WorkflowNodeCopy.CloneForInsertion(
            action.Model,
            EnumerateActions().Select(candidate => candidate.Alias));
        var clone = new WorkflowActionViewModel(clonedNode, _localization);
        AttachAction(clone);
        destination.Insert(Math.Max(0, Math.Min(index, destination.Count)), clone);
        SelectAction(clone);
        _pasteDestination = destination;
        _pasteIndex = destination.IndexOf(clone) + 1;
        IsDirty = true;
        return true;
    }

    private async Task NewAsync()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        if (!await ConfirmUnsavedChangesAsync())
        {
            return;
        }

        ReplaceDocument(new WorkflowDocument { Name = _localization["DocumentUntitled"] }, null, false);
        _undo.Clear();
        _redo.Clear();
        StatusMessage = string.Empty;
    }

    private async Task OpenAsync()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        if (!await ConfirmUnsavedChangesAsync())
        {
            return;
        }

        var path = _fileDialogs.ShowOpenWorkflowDialog();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var document = await _documentService.LoadAsync(path!);
            ReplaceDocument(document, path, false);
            _undo.Clear();
            _redo.Clear();
            StatusMessage = path!;
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not open workflow {Path}", path);
            StatusMessage = exception.Message;
            StatusIsError = true;
        }
    }

    private async Task SaveAsync()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        await SaveDocumentAsync(false);
    }

    private async Task SaveAsAsync()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        await SaveDocumentAsync(true);
    }

    private async Task<bool> SaveDocumentAsync(bool forceSaveAs)
    {
        if (!forceSaveAs && !string.IsNullOrWhiteSpace(_documentPath))
        {
            return await SaveToPathAsync(_documentPath!);
        }

        var suggested = MakeSafeFileName(DocumentName) + ".drillflow.json";
        var path = _fileDialogs.ShowSaveWorkflowDialog(suggested);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        _document.Name = Path.GetFileName(path).Replace(".drillflow.json", string.Empty);
        var saved = await SaveToPathAsync(path!);
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(DocumentDisplayName));
        return saved;
    }

    private async Task<bool> SaveToPathAsync(string path)
    {
        try
        {
            await _documentService.SaveAsync(path, _document);
            _documentPath = path;
            IsDirty = false;
            StatusMessage = path;
            StatusIsError = false;
            OnPropertyChanged(nameof(DocumentPath));
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not save workflow {Path}", path);
            StatusMessage = exception.Message;
            StatusIsError = true;
            return false;
        }
    }

    private async Task<bool> ConfirmUnsavedChangesAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        return _fileDialogs.ConfirmUnsavedChanges() switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save => await SaveDocumentAsync(false),
            _ => false
        };
    }

    private void Undo()
    {
        if (_undo.Count == 0 || !IsWorkflowEditingEnabled)
        {
            return;
        }

        _redo.Push(_documentService.Serialize(_document));
        RestoreSnapshot(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0 || !IsWorkflowEditingEnabled)
        {
            return;
        }

        _undo.Push(_documentService.Serialize(_document));
        RestoreSnapshot(_redo.Pop());
    }

    private void RestoreSnapshot(string json)
    {
        var path = _documentPath;
        ReplaceDocument(_documentService.Deserialize(json), path, true);
        NotifyCommandStates();
    }

    private bool ValidateWorkflow(bool updateStatus)
    {
        var valid = true;
        var all = EnumerateActions().ToArray();
        foreach (var action in all)
        {
            valid &= action.Validate();
        }

        foreach (var group in all.GroupBy(action => action.Alias, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Count() <= 1)
            {
                continue;
            }

            valid = false;
            foreach (var duplicate in group)
            {
                duplicate.SetExternalAliasError(string.Equals(_localization.EffectiveLanguage, "en-US", StringComparison.OrdinalIgnoreCase)
                    ? "Aliases must be unique."
                    : "별칭은 워크플로 안에서 고유해야 합니다.");
            }
        }

        var coreResult = _workflowValidator.Validate(_document);
        var coreErrors = coreResult.Issues
            .Where(issue => issue.Severity == ValidationSeverity.Error)
            .ToArray();
        valid &= coreResult.IsValid;

        if (updateStatus)
        {
            if (valid)
            {
                StatusMessage = _localization["ValidationPassed"];
            }
            else if (coreErrors.Length > 0)
            {
                StatusMessage = string.Format(
                    _localization["ValidationIssueSummary"],
                    coreErrors.Length,
                    FormatValidationDetail(coreErrors[0]));

                if (coreErrors[0].NodeId is Guid nodeId)
                {
                    SelectAction(all.FirstOrDefault(action => action.Id == nodeId));
                }
            }
            else
            {
                StatusMessage = _localization["ValidationFailed"];
                SelectAction(all.FirstOrDefault(action =>
                    action.HasAliasError || action.Parameters.Any(parameter => parameter.HasError)));
            }

            StatusIsError = !valid;
        }

        return valid;
    }

    private async Task RunAsync()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        if (!ValidateWorkflow(true))
        {
            return;
        }

        foreach (var action in Actions)
        {
            action.ClearRuntime();
        }

        try
        {
            // Run a deep snapshot so late bindings, automation, or an event race cannot mutate
            // parameters or structure after validation and alter a physical equipment request.
            var executionDocument = _documentService.Deserialize(_documentService.Serialize(_document));
            await _execution.RunAsync(executionDocument);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Workflow run failed");
            StatusMessage = exception.Message;
            StatusIsError = true;
            RunState = WorkflowRunState.Faulted;
        }
    }

    private async Task RunSelectedAsync()
    {
        var selected = SelectedAction;
        if (selected is null || !selected.IsNodeEnabled || !IsWorkflowEditingEnabled)
        {
            return;
        }

        if (!selected.Validate())
        {
            StatusMessage = _localization["ValidationFailed"];
            StatusIsError = true;
            return;
        }

        try
        {
            // Preserve node ids so execution events and results continue to light up the
            // cards being inspected, while isolating the runner from editor mutations.
            var selectionDocument = new WorkflowDocument
            {
                Name = selected.Alias,
                Nodes = new List<WorkflowNode> { selected.Model }
            };
            var executionDocument = _documentService.Deserialize(
                _documentService.Serialize(selectionDocument));
            foreach (var node in executionDocument.EnumerateNodesDepthFirst())
            {
                // "Run only this Action" is an explicit command and must not immediately
                // stop at an authored breakpoint on the same subtree.
                node.HasBreakpoint = false;
            }

            var validation = _workflowValidator.Validate(executionDocument);
            if (!validation.IsValid)
            {
                var issue = validation.Issues.FirstOrDefault(candidate =>
                    candidate.Severity == ValidationSeverity.Error);
                StatusMessage = issue is null
                    ? _localization["ValidationFailed"]
                    : string.Format(
                        _localization["ValidationIssueSummary"],
                        validation.Issues.Count(candidate => candidate.Severity == ValidationSeverity.Error),
                        FormatValidationDetail(issue));
                StatusIsError = true;
                return;
            }

            foreach (var action in Actions)
            {
                action.ClearRuntime();
            }

            await _execution.RunAsync(executionDocument);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Selected workflow action run failed");
            StatusMessage = exception.Message;
            StatusIsError = true;
            RunState = WorkflowRunState.Faulted;
        }
    }

    private void Continue() => _execution.Continue();

    private void Step() => _execution.Step();

    private void Stop()
    {
        if (RunState == WorkflowRunState.Stopping)
        {
            _execution.ForceStop();
            StatusMessage = _localization["ForceStopping"];
            StatusIsError = false;
            return;
        }

        _execution.RequestStop();
        RunState = WorkflowRunState.Stopping;
        StatusMessage = _localization["StatusStopping"];
        StatusIsError = false;
    }

    private async Task TestResponseAsync()
    {
        var selected = SelectedAction;
        if (selected is null || !IsEquipmentAction(selected.Kind))
        {
            return;
        }

        try
        {
            if (await _responseSimulationDialogs.ShowAsync(selected))
            {
                StatusMessage = _localization["ResponseTestPublished"];
                StatusIsError = false;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Response simulation failed for action {ActionKey}", selected.Alias);
            StatusMessage = _localization["ResponseTestFailed"] + " " + exception.Message;
            StatusIsError = true;
        }
    }

    private void OpenExchangeFolder()
    {
        try
        {
            var path = _exchangeFolderLauncher.Open();
            StatusMessage = string.Format(_localization["ExchangeFolderOpened"], path);
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not open the configured equipment exchange directory");
            StatusMessage = _localization["ExchangeFolderOpenFailed"] + " " + exception.Message;
            StatusIsError = true;
        }
    }

    private void ToggleBreakpoint()
    {
        if (SelectedAction is null || !IsWorkflowEditingEnabled)
        {
            return;
        }

        CaptureUndoCheckpoint();
        SelectedAction.HasBreakpoint = !SelectedAction.HasBreakpoint;
        IsDirty = true;
        NotifyCommandStates();
    }

    private void ClearBreakpoints()
    {
        if (!IsWorkflowEditingEnabled)
        {
            return;
        }

        CaptureUndoCheckpoint();
        foreach (var action in EnumerateActions())
        {
            action.HasBreakpoint = false;
        }

        IsDirty = true;
        NotifyCommandStates();
    }

    private void CopySelected()
    {
        if (SelectedAction is null)
        {
            return;
        }

        var clipboardDocument = new WorkflowDocument
        {
            Name = "Clipboard",
            Nodes = new List<WorkflowNode> { SelectedAction.Model }
        };
        _clipboardSnapshot = _documentService.Serialize(clipboardDocument);
        StatusMessage = string.Format(_localization["ActionCopied"], SelectedAction.Alias);
        StatusIsError = false;
        NotifyCommandStates();
    }

    private void CutSelected()
    {
        if (SelectedAction is null || !IsWorkflowEditingEnabled)
        {
            return;
        }

        CopySelected();
        DeleteSelected();
    }

    private void Paste()
    {
        if (_clipboardSnapshot is null || !IsWorkflowEditingEnabled)
        {
            return;
        }

        ObservableCollection<WorkflowActionViewModel> destination;
        int index;
        if (_pasteDestination is not null && IsKnownCollection(_pasteDestination))
        {
            destination = _pasteDestination;
            index = _pasteIndex;
        }
        else if (SelectedAction is not null
                 && FindCollectionContaining(SelectedAction) is { } selectedParent)
        {
            destination = selectedParent;
            index = selectedParent.IndexOf(SelectedAction) + 1;
        }
        else
        {
            destination = Actions;
            index = Actions.Count;
        }

        WorkflowDocument clipboardDocument;
        try
        {
            clipboardDocument = _documentService.Deserialize(_clipboardSnapshot);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The in-memory Action clipboard could not be read");
            _clipboardSnapshot = null;
            StatusMessage = _localization["ClipboardInvalid"];
            StatusIsError = true;
            NotifyCommandStates();
            return;
        }

        var source = clipboardDocument.Nodes?.SingleOrDefault();
        if (source is null)
        {
            _clipboardSnapshot = null;
            StatusMessage = _localization["ClipboardInvalid"];
            StatusIsError = true;
            NotifyCommandStates();
            return;
        }

        CaptureUndoCheckpoint();
        var clonedNode = WorkflowNodeCopy.CloneForInsertion(
            source,
            EnumerateActions().Select(candidate => candidate.Alias));
        var clone = new WorkflowActionViewModel(clonedNode, _localization);
        AttachAction(clone);
        index = Math.Max(0, Math.Min(index, destination.Count));
        destination.Insert(index, clone);
        _pasteDestination = destination;
        _pasteIndex = index + 1;
        SelectAction(clone);
        IsDirty = true;
        StatusMessage = string.Format(_localization["ActionPasted"], clone.Alias);
        StatusIsError = false;
    }

    private void ToggleEnabled()
    {
        if (SelectedAction is null || !IsWorkflowEditingEnabled)
        {
            return;
        }

        CaptureUndoCheckpoint();
        SelectedAction.IsNodeEnabled = !SelectedAction.IsNodeEnabled;
        IsDirty = true;
        NotifyCommandStates();
    }

    private void DeleteSelected()
    {
        if (SelectedAction is null || !IsWorkflowEditingEnabled)
        {
            return;
        }

        var parent = FindCollectionContaining(SelectedAction);
        if (parent is null)
        {
            return;
        }

        CaptureUndoCheckpoint();
        parent.Remove(SelectedAction);
        SelectedAction = null;
        IsDirty = true;
    }

    private void AppendToolboxItem(ToolboxItemViewModel? item)
    {
        if (item is not null && IsWorkflowEditingEnabled)
        {
            CreateAndInsert(item.Kind, Actions, Actions.Count);
        }
    }

    private void AddElseIf()
    {
        if (SelectedAction?.Model is not ConditionalNode || !IsWorkflowEditingEnabled)
        {
            return;
        }

        CaptureUndoCheckpoint();
        var branch = new ConditionalBranch
        {
            Kind = ConditionalBranchKind.ElseIf,
            Condition = ParameterBinding.Expression("true")
        };
        var vm = new WorkflowBranchViewModel(branch, _localization);
        AttachBranch(vm);
        var elseIndex = SelectedAction.Branches.ToList().FindIndex(candidate => candidate.Kind == ConditionalBranchKind.Else);
        SelectedAction.Branches.Insert(elseIndex < 0 ? SelectedAction.Branches.Count : elseIndex, vm);
        IsDirty = true;
        NotifyCommandStates();
    }

    private bool CanAddElseIf() => SelectedAction?.Model is ConditionalNode && IsWorkflowEditingEnabled;

    private void AddElse()
    {
        if (!IsWorkflowEditingEnabled
            || SelectedAction?.Model is not ConditionalNode
            || SelectedAction.Branches.Any(branch => branch.Kind == ConditionalBranchKind.Else))
        {
            return;
        }

        CaptureUndoCheckpoint();
        var branch = new WorkflowBranchViewModel(
            new ConditionalBranch { Kind = ConditionalBranchKind.Else, Condition = null },
            _localization);
        AttachBranch(branch);
        SelectedAction.Branches.Add(branch);
        IsDirty = true;
        NotifyCommandStates();
    }

    private bool CanAddElse() => SelectedAction?.Model is ConditionalNode
        && !SelectedAction.Branches.Any(branch => branch.Kind == ConditionalBranchKind.Else)
        && IsWorkflowEditingEnabled;

    private void ReplaceDocument(WorkflowDocument document, string? path, bool dirty)
    {
        _document = document ?? new WorkflowDocument();
        _document.Nodes ??= new List<WorkflowNode>();
        _documentPath = path;

        _suppressCollectionSync = true;
        try
        {
            Actions.Clear();
            foreach (var node in _document.Nodes)
            {
                var action = new WorkflowActionViewModel(node, _localization);
                action.SetEditingEnabled(IsWorkflowEditingEnabled);
                AttachAction(action);
                Actions.Add(action);
            }
        }
        finally
        {
            _suppressCollectionSync = false;
        }

        SelectedAction = null;
        _pasteDestination = null;
        _pasteIndex = 0;
        IsDirty = dirty;
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(DocumentDisplayName));
        OnPropertyChanged(nameof(DocumentPath));
        NotifyCommandStates();
    }

    private void AttachAction(WorkflowActionViewModel action)
    {
        action.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(WorkflowActionViewModel.Alias)
                or nameof(WorkflowActionViewModel.HasBreakpoint)
                or nameof(WorkflowActionViewModel.IsNodeEnabled))
            {
                IsDirty = true;
                NotifyCommandStates();
            }
        };

        foreach (var parameter in action.Parameters)
        {
            parameter.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(ActionParameterViewModel.Value))
                {
                    IsDirty = true;
                }
            };
        }

        foreach (var child in action.Children)
        {
            AttachAction(child);
        }

        foreach (var branch in action.Branches)
        {
            AttachBranch(branch);
        }
    }

    private void AttachBranch(WorkflowBranchViewModel branch)
    {
        if (branch.Condition is not null)
        {
            branch.Condition.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(ActionParameterViewModel.Value))
                {
                    IsDirty = true;
                }
            };
        }

        foreach (var child in branch.Children)
        {
            AttachAction(child);
        }
    }

    private void OnRootActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionSync)
        {
            return;
        }

        _document.Nodes.Clear();
        _document.Nodes.AddRange(Actions.Select(action => action.Model));
        IsDirty = true;
        NotifyCommandStates();
    }

    private void OnRunStateChanged(object? sender, WorkflowRunStateChangedEventArgs e)
    {
        Dispatch(() =>
        {
            RunState = e.State;
            StatusMessage = e.State == WorkflowRunState.Faulted && e.Exception is not null
                ? RuntimeStatusText + ": " + e.Exception.Message
                : RuntimeStatusText;
            StatusIsError = e.State == WorkflowRunState.Faulted;

            if (e.State is WorkflowRunState.Completed or WorkflowRunState.Stopped or WorkflowRunState.Faulted)
            {
                foreach (var action in EnumerateActions())
                {
                    action.IsCurrent = false;
                }

                _terminalStateWaiter?.TrySetResult(true);
            }
        });
    }

    private void OnNodeStateChanged(object? sender, WorkflowNodeStateChangedEventArgs e)
    {
        Dispatch(() =>
        {
            foreach (var candidate in EnumerateActions())
            {
                candidate.IsCurrent = candidate.Id == e.Node.Id
                    && e.State is WorkflowNodeExecutionState.Running or WorkflowNodeExecutionState.Paused;
            }

            var action = EnumerateActions().FirstOrDefault(candidate => candidate.Id == e.Node.Id);
            if (action is null)
            {
                return;
            }

            action.RuntimeState = e.State;
            if (e.Result is not null)
            {
                action.AddResult(e.Result);
            }

            if (e.Exception is not null)
            {
                StatusMessage = e.Exception.Message;
                StatusIsError = true;
            }
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(DocumentDisplayName));
        OnPropertyChanged(nameof(RuntimeStatusText));
        NotifyCommandStates();
    }

    private IEnumerable<WorkflowActionViewModel> EnumerateActions()
    {
        return Actions.SelectMany(action => action.EnumerateDepthFirst());
    }

    private static ObservableCollection<WorkflowActionViewModel>? FindCollectionContaining(
        ObservableCollection<WorkflowActionViewModel> collection,
        WorkflowActionViewModel action)
    {
        if (collection.Contains(action))
        {
            return collection;
        }

        foreach (var candidate in collection)
        {
            var childResult = FindCollectionContaining(candidate.Children, action);
            if (childResult is not null)
            {
                return childResult;
            }

            foreach (var branch in candidate.Branches)
            {
                var branchResult = FindCollectionContaining(branch.Children, action);
                if (branchResult is not null)
                {
                    return branchResult;
                }
            }
        }

        return null;
    }

    private static bool IsCollectionInside(
        WorkflowActionViewModel action,
        ObservableCollection<WorkflowActionViewModel> collection)
    {
        if (ReferenceEquals(action.Children, collection)
            || action.Branches.Any(branch => ReferenceEquals(branch.Children, collection)))
        {
            return true;
        }

        return action.Children.Any(child => IsCollectionInside(child, collection))
               || action.Branches.SelectMany(branch => branch.Children).Any(child => IsCollectionInside(child, collection));
    }

    private bool IsKnownCollection(ObservableCollection<WorkflowActionViewModel> collection)
    {
        return ReferenceEquals(Actions, collection)
               || Actions.Any(action => ContainsCollection(action, collection));
    }

    private static bool ContainsCollection(
        WorkflowActionViewModel action,
        ObservableCollection<WorkflowActionViewModel> collection)
    {
        if (ReferenceEquals(action.Children, collection)
            || action.Branches.Any(branch => ReferenceEquals(branch.Children, collection)))
        {
            return true;
        }

        return action.Children.Any(child => ContainsCollection(child, collection))
               || action.Branches
                   .SelectMany(branch => branch.Children)
                   .Any(child => ContainsCollection(child, collection));
    }

    private void NotifyCommandStates()
    {
        NewCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        RunSelectedCommand.NotifyCanExecuteChanged();
        TestResponseCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        StepCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ToggleBreakpointCommand.NotifyCanExecuteChanged();
        ClearBreakpointsCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CopySelectedCommand.NotifyCanExecuteChanged();
        CutSelectedCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        AddToolboxItemCommand.NotifyCanExecuteChanged();
        AddElseIfCommand.NotifyCanExecuteChanged();
        AddElseCommand.NotifyCanExecuteChanged();
    }

    private void SetWorkflowEditingEnabled(bool enabled)
    {
        foreach (var action in Actions)
        {
            action.SetEditingEnabled(enabled);
        }
    }

    private static bool IsBusyState(WorkflowRunState state) =>
        state is WorkflowRunState.Validating
            or WorkflowRunState.Running
            or WorkflowRunState.Paused
            or WorkflowRunState.Stopping;

    private static bool IsEquipmentAction(WorkflowNodeKind kind) =>
        kind is WorkflowNodeKind.Move
            or WorkflowNodeKind.Measure
            or WorkflowNodeKind.Drill
            or WorkflowNodeKind.Abort;

    private string FormatValidationDetail(ValidationIssue issue)
    {
        if (string.Equals(_localization.EffectiveLanguage, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return issue.Message;
        }

        return string.IsNullOrWhiteSpace(issue.Path)
            ? issue.Code
            : issue.Code + " (" + issue.Path + ")";
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "workflow" : value;
    }

    private static bool IsTerminalState(WorkflowRunState state) =>
        state is WorkflowRunState.Idle
            or WorkflowRunState.Completed
            or WorkflowRunState.Stopped
            or WorkflowRunState.Faulted;

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
