namespace DrillFlow.Desktop.Services;

/// <summary>
/// Opens the configured or explicitly supplied equipment exchange directory in Windows Explorer.
/// </summary>
public interface IExchangeFolderLauncher
{
    string Open();

    string Open(string directory);
}
