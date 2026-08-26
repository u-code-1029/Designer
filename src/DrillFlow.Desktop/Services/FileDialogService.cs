using System;
using System.Windows.Interop;
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

        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == true ? dialog.FileName : null;
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

        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == true ? dialog.FileName : null;
    }

    public string? ShowSelectFolderDialog(string initialFolder)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _localization["SelectExchangeFolder"],
            ShowNewFolderButton = true,
            SelectedPath = System.IO.Directory.Exists(initialFolder) ? initialFolder : string.Empty
        };

        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null
            ? dialog.ShowDialog()
            : dialog.ShowDialog(new NativeWindowOwner(new WindowInteropHelper(owner).Handle));
        return result == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private sealed class NativeWindowOwner : Forms.IWin32Window
    {
        public NativeWindowOwner(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}
