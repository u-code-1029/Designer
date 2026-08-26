namespace DrillFlow.Desktop.Services;

public interface IFileDialogService
{
    string? ShowOpenWorkflowDialog();

    string? ShowSaveWorkflowDialog(string suggestedFileName);

    string? ShowSelectFolderDialog(string initialFolder);
}
