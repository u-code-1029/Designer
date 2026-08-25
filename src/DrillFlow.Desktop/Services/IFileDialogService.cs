namespace DrillFlow.Desktop.Services;

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel
}

public interface IFileDialogService
{
    string? ShowOpenWorkflowDialog();

    string? ShowSaveWorkflowDialog(string suggestedFileName);

    string? ShowSelectFolderDialog(string initialFolder);

    UnsavedChangesChoice ConfirmUnsavedChanges();
}
