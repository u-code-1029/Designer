using System.Windows;
using DrillFlow.Desktop.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DrillFlow.Desktop.Services;

public sealed class FileDialogService : IFileDialogService
{
    private const string WorkflowFilter = "DrillFlow workflow (*.drillflow.json)|*.drillflow.json|JSON (*.json)|*.json|All files (*.*)|*.*";

    private readonly ILocalizationService _localization;

    public FileDialogService(ILocalizationService localization)
    {
        _localization = localization;
    }

    public string? ShowOpenWorkflowDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = WorkflowFilter,
            CheckFileExists = true,
            Multiselect = false,
            Title = _localization["Open"]
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowSaveWorkflowDialog(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = WorkflowFilter,
            AddExtension = true,
            DefaultExt = ".drillflow.json",
            FileName = suggestedFileName,
            Title = _localization["SaveAs"]
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowSelectFolderDialog(string initialFolder)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _localization["SelectExchangeFolder"],
            ShowNewFolderButton = true,
            SelectedPath = System.IO.Directory.Exists(initialFolder) ? initialFolder : string.Empty
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public UnsavedChangesChoice ConfirmUnsavedChanges()
    {
        var result = MessageBox.Show(
            _localization["UnsavedChangesPrompt"],
            _localization["UnsavedChangesTitle"],
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesChoice.Save,
            MessageBoxResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel
        };
    }
}
