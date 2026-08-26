using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DrillFlow.Desktop.Views;

public partial class StartupDialogWindow : FluentWindow
{
    public StartupDialogWindow()
    {
        InitializeComponent();
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "DrillFlow Designer" : title;
        Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var host = ContentDialogHost.GetForWindow(this)
                   ?? throw new InvalidOperationException("The startup ContentDialog host is unavailable.");
        var dialog = new ContentDialog(host)
        {
            Title = Title,
            Content = new TextBlock
            {
                Text = message ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500,
                Margin = new Thickness(0, 4, 0, 4)
            },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            DialogWidth = 540
        };

        await dialog.ShowAsync(CancellationToken.None);
        Close();
    }
}
