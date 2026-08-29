using System;
using System.Linq;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Wpf.Ui.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DrillFlow.Desktop.ViewModels;

public enum ToolboxItemCategory
{
    Equipment,
    Designer
}

public sealed class ToolboxItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string _titleKey;
    private readonly string _descriptionKey;

    public ToolboxItemViewModel(
        WorkflowNodeKind kind,
        string titleKey,
        string descriptionKey,
        SymbolRegular icon,
        ToolboxItemCategory category,
        ILocalizationService localization)
    {
        Kind = kind;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        Icon = icon;
        Category = category;
        _localization = localization;
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(CategoryTitle));
            OnPropertyChanged(nameof(MetadataLabel));
        };
    }

    public WorkflowNodeKind Kind { get; }

    public string Title => _localization[_titleKey];

    public string Description => _localization[_descriptionKey];

    public SymbolRegular Icon { get; }

    public ToolboxItemCategory Category { get; }

    public string CategoryTitle => _localization[
        Category == ToolboxItemCategory.Equipment ? "EquipmentActions" : "FlowActions"];

    /// <summary>
    /// The compact identifier users see in JSON/XML contracts or designer expressions.
    /// Keeping it beside the localized title makes equipment terminology searchable
    /// without adding another translated description line to the toolbox card.
    /// </summary>
    public string ActionToken => Kind switch
    {
        WorkflowNodeKind.Stage => "stage",
        WorkflowNodeKind.Camera => "camera",
        WorkflowNodeKind.Focus => "focus",
        WorkflowNodeKind.Integration => "integration",
        WorkflowNodeKind.Live => "live",
        WorkflowNodeKind.Om => "om",
        WorkflowNodeKind.Lens => "lens",
        WorkflowNodeKind.AutoContrastBrightness => "acb",
        WorkflowNodeKind.Abort => "abort",
        WorkflowNodeKind.Http => "http",
        WorkflowNodeKind.Delay => "delay",
        WorkflowNodeKind.Repeat => "repeat",
        _ => "if"
    };

    public string MetadataLabel => ActionToken + "  ·  " + CategoryTitle;

    public bool MatchesSearch(string? searchText)
    {
        var terms = (searchText ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return true;
        }

        var searchableText = string.Join(
            " ",
            Title,
            Description,
            CategoryTitle,
            ActionToken,
            Kind.ToString());
        return terms.All(term => searchableText.IndexOf(
            term,
            StringComparison.CurrentCultureIgnoreCase) >= 0);
    }
}
