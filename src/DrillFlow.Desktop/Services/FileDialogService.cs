using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DrillFlow.Desktop.Services;

public sealed class FileDialogService : IFileDialogService
{
    private const string WorkflowFilter = "DrillFlow workflow (*.drillflow.json)|*.drillflow.json|JSON (*.json)|*.json|All files (*.*)|*.*";

    private readonly ILocalizationService _localization;
    private readonly ILogger<FileDialogService> _logger;

    public FileDialogService(
        ILocalizationService localization,
        ILogger<FileDialogService> logger)
    {
        _localization = localization;
        _logger = logger;
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

    public string? ShowSaveImageDialog(string sourceImagePath, string detectedExtension)
    {
        var extension = Path.GetExtension(sourceImagePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            extension = NormalizeImageExtension(detectedExtension);
        }

        var normalizedExtension = extension.TrimStart('.');
        var sourceFileName = Path.GetFileNameWithoutExtension(sourceImagePath);
        var suggestedName = string.IsNullOrWhiteSpace(sourceFileName)
            ? "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
            : sourceFileName;

        var dialog = new SaveFileDialog
        {
            Filter = $"{normalizedExtension.ToUpperInvariant()} (*.{normalizedExtension})|*.{normalizedExtension}",
            AddExtension = true,
            DefaultExt = extension,
            FileName = suggestedName + extension,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Title = _localization["SaveCapturedImage"]
        };

        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == true ? dialog.FileName : null;
    }

    private static string NormalizeImageExtension(string extension)
    {
        var normalized = (extension ?? string.Empty).Trim();
        if (!normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = "." + normalized.TrimStart('*', '.');
        }

        return normalized.Length > 1
               && normalized.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? normalized
            : ".png";
    }

    public string? ShowSelectFolderDialog(string initialFolder)
    {
        try
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            var ownerHandle = IntPtr.Zero;
            if (owner is not null)
            {
                var interopHelper = new WindowInteropHelper(owner);
                ownerHandle = interopHelper.Handle;
                if (ownerHandle == IntPtr.Zero)
                {
                    ownerHandle = interopHelper.EnsureHandle();
                }
            }

            return ShellFolderPicker.Show(
                ownerHandle,
                _localization["SelectExchangeFolder"],
                initialFolder);
        }
        catch (Exception exception) when (
            exception is COMException ||
            exception is InvalidOperationException)
        {
            _logger.LogError(exception, "Failed to show the Windows folder picker.");
            return null;
        }
    }
}
