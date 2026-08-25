namespace DrillFlow.Desktop.Services;

/// <summary>
/// Opens the currently configured equipment exchange directory in Windows Explorer.
/// </summary>
public interface IExchangeFolderLauncher
{
    string Open();
}
