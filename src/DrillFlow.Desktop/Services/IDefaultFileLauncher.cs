namespace DrillFlow.Desktop.Services;

/// <summary>
/// Opens a file with its Windows shell-associated default application.
/// </summary>
public interface IDefaultFileLauncher
{
    string Open(string filePath);
}
