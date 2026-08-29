namespace DrillFlow.Desktop.Services;

/// <summary>
/// Opens Windows Explorer at the request or response pathname represented by a terminal link.
/// </summary>
public interface IEquipmentExchangePathLauncher
{
    string OpenFileLocation(string filePath);
}
