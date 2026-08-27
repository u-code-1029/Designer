using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;

namespace DrillFlow.Desktop.ViewModels;

public sealed class WorkflowBranchViewModel : ObservableObject
{
    private readonly ConditionalBranch _branch;
    private readonly ILocalizationService _localization;

    public WorkflowBranchViewModel(
        ConditionalBranch branch,
        ILocalizationService localization,
        ILiveImageDecoder imageDecoder)
    {
        _branch = branch ?? throw new ArgumentNullException(nameof(branch));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        imageDecoder = imageDecoder ?? throw new ArgumentNullException(nameof(imageDecoder));
        Children = new ObservableCollection<WorkflowActionViewModel>();

        if (branch.Body is not null)
        {
            foreach (var child in branch.Body)
            {
                Children.Add(new WorkflowActionViewModel(child, localization, imageDecoder));
            }
        }

        if (branch.Kind != ConditionalBranchKind.Else)
        {
            branch.Condition ??= ParameterBinding.Literal("true");
            Condition = new ActionParameterViewModel(
                "condition",
                "ParamCondition",
                branch.Condition,
                WorkflowNodeKind.Conditional,
                localization);
        }

        Children.CollectionChanged += OnChildrenChanged;
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ConditionalBranch Model => _branch;

    public ConditionalBranchKind Kind => _branch.Kind;

    public string Title => _localization[Kind switch
    {
        ConditionalBranchKind.If => "BranchIf",
        ConditionalBranchKind.ElseIf => "BranchElseIf",
        _ => "BranchElse"
    }];

    public ActionParameterViewModel? Condition { get; }

    public ObservableCollection<WorkflowActionViewModel> Children { get; }

    public bool IsEditingEnabled { get; private set; } = true;

    public bool Validate()
    {
        var valid = Condition?.Validate() ?? true;
        foreach (var child in Children)
        {
            valid &= child.Validate();
        }

        return valid;
    }

    public void SetEditingEnabled(bool enabled)
    {
        if (IsEditingEnabled != enabled)
        {
            IsEditingEnabled = enabled;
            OnPropertyChanged(nameof(IsEditingEnabled));
        }

        Condition?.SetEditingEnabled(enabled);
        foreach (var child in Children)
        {
            child.SetEditingEnabled(enabled);
        }
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _branch.Body.Clear();
        foreach (var child in Children)
        {
            _branch.Body.Add(child.Model);
        }
    }
}
