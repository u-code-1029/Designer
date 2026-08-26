using System.Threading.Tasks;

namespace DrillFlow.Desktop.Services;

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel
}

public interface IUserDialogService
{
    Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync();

    Task ShowMessageAsync(string title, string message);
}
