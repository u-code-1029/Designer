using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Desktop.Services;

namespace DrillFlow.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public MainWindowViewModel(ILocalizationService localization)
    {
        _localization = localization;
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Title => _localization["AppTitle"];
}
