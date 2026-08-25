using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Views;

public partial class SettingsPage : System.Windows.Controls.Page
{
    public SettingsPage(SettingsPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
