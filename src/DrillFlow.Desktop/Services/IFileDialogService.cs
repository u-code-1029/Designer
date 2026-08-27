namespace DrillFlow.Desktop.Services;

public interface IFileDialogService
{
    string? ShowOpenWorkflowDialog();

    string? ShowSaveWorkflowDialog(string suggestedFileName);

    string? ShowSaveImageDialog(string sourceImagePath, string detectedExtension);

    string? ShowSelectFolderDialog(string initialFolder);
}
