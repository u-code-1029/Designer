using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DrillFlow.Desktop.Services;

public sealed class UserDialogService : IUserDialogService
{
    private readonly ILocalizationService _localization;
    private readonly IContentDialogGate _dialogGate;

    public UserDialogService(
        ILocalizationService localization,
        IContentDialogGate dialogGate)
    {
        _localization = localization;
        _dialogGate = dialogGate;
    }

    public async Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync()
    {
        using (await _dialogGate.EnterAsync().ConfigureAwait(true))
        {
            var dialog = new ContentDialog(GetHost())
            {
                Title = _localization["UnsavedChangesTitle"],
                Content = CreateMessage(_localization["UnsavedChangesPrompt"]),
                PrimaryButtonText = _localization["Save"],
                SecondaryButtonText = _localization["Discard"],
                CloseButtonText = _localization["Cancel"],
                DefaultButton = ContentDialogButton.Primary,
                DialogWidth = 520
            };

            var result = await dialog.ShowAsync(CancellationToken.None);
            return result switch
            {
                ContentDialogResult.Primary => UnsavedChangesChoice.Save,
                ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
                _ => UnsavedChangesChoice.Cancel
            };
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        using (await _dialogGate.EnterAsync().ConfigureAwait(true))
        {
            var dialog = new ContentDialog(GetHost())
            {
                Title = title ?? string.Empty,
                Content = CreateMessage(message),
                CloseButtonText = _localization["OK"],
                DefaultButton = ContentDialogButton.Close,
                DialogWidth = 520
            };

            await dialog.ShowAsync(CancellationToken.None);
        }
    }

    private static ContentDialogHost GetHost()
    {
        var mainWindow = System.Windows.Application.Current?.MainWindow
                         ?? throw new InvalidOperationException("The main application window is unavailable.");
        return ContentDialogHost.GetForWindow(mainWindow)
               ?? throw new InvalidOperationException("The main ContentDialog host is unavailable.");
    }

    private static TextBlock CreateMessage(string? message)
    {
        return new TextBlock
        {
            Text = message ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 4, 0, 4)
        };
    }
}
