using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Application.Execution;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.ViewModels;

public sealed class WorkflowActionViewModel : ObservableObject
{
    private static readonly Regex AliasPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private readonly ILocalizationService _localization;
    private bool _isSelected;
    private bool _isCurrent;
    private bool _isEditingEnabled = true;
    private string _aliasValidationMessage = string.Empty;
    private WorkflowNodeExecutionState _runtimeState = WorkflowNodeExecutionState.Waiting;

    public WorkflowActionViewModel(WorkflowNode model, ILocalizationService localization)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _localization = localization;
        Parameters = new ObservableCollection<ActionParameterViewModel>();
        Children = new ObservableCollection<WorkflowActionViewModel>();
        Branches = new ObservableCollection<WorkflowBranchViewModel>();
        Results = new ObservableCollection<RuntimeResultViewModel>();

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
                Children.Add(new WorkflowActionViewModel(child, localization));
            }

            Children.CollectionChanged += OnRepeatChildrenChanged;
        }
        else if (model is ConditionalNode conditional)
        {
            foreach (var branch in conditional.Branches)
            {
                Branches.Add(new WorkflowBranchViewModel(branch, localization));
            }

            Branches.CollectionChanged += OnBranchesChanged;
        }

        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(RuntimeStateText));
        };
    }

    public WorkflowNode Model { get; }

    public Guid Id => Model.Id;

    public WorkflowNodeKind Kind => Model.Kind;

    public string Title => _localization[Model.Kind switch
    {
        WorkflowNodeKind.Move => "ActionMove",
        WorkflowNodeKind.Measure => "ActionMeasure",
        WorkflowNodeKind.Drill => "ActionDrill",
        WorkflowNodeKind.Abort => "ActionAbort",
        WorkflowNodeKind.Delay => "ActionDelay",
        WorkflowNodeKind.Repeat => "ActionRepeat",
        _ => "ActionConditional"
    }];

    public SymbolRegular Icon => Model.Kind switch
    {
        WorkflowNodeKind.Move => SymbolRegular.ArrowMove20,
        WorkflowNodeKind.Measure => SymbolRegular.Ruler20,
        WorkflowNodeKind.Drill => SymbolRegular.Toolbox20,
        WorkflowNodeKind.Abort => SymbolRegular.Stop20,
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
            ValidateAlias();
        }
    }

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

    public ObservableCollection<ActionParameterViewModel> Parameters { get; }

    public ObservableCollection<WorkflowActionViewModel> Children { get; }

    public ObservableCollection<WorkflowBranchViewModel> Branches { get; }

    public ObservableCollection<RuntimeResultViewModel> Results { get; }

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

    public void SetExternalAliasError(string message)
    {
        AliasValidationMessage = message ?? string.Empty;
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
        Results.Add(new RuntimeResultViewModel(result));
        OnPropertyChanged(nameof(Results));
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

    private static string GetParameterLabelKey(string name) => name switch
    {
        "move_mode" => "ParamMoveMode",
        "move_x" => "ParamMoveX",
        "move_y" => "ParamMoveY",
        "thickness" => "ParamThickness",
        "drill_result_path" => "ParamResultPath",
        "milliseconds" => "ParamDelay",
        "count" => "ParamCount",
        "condition" => "ParamCondition",
        _ => name
    };
}
