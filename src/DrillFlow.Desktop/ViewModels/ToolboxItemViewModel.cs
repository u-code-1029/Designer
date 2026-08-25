using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Wpf.Ui.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DrillFlow.Desktop.ViewModels;

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
        ILocalizationService localization)
    {
        Kind = kind;
        _titleKey = titleKey;
        _descriptionKey = descriptionKey;
        Icon = icon;
        _localization = localization;
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
        };
    }

    public WorkflowNodeKind Kind { get; }

    public string Title => _localization[_titleKey];

    public string Description => _localization[_descriptionKey];

    public SymbolRegular Icon { get; }
}
