namespace DrillFlow.Desktop.Services;

/// <summary>
/// Creates short-lived images used by the response simulator. Implementations own the generated
/// files and remove them when the application host is disposed.
/// </summary>
public interface ITemporaryResponseImageService
{
    TemporaryResponseImage CreateTemporaryImage();

    /// <summary>
    /// Releases an image created by this service. Unknown paths are ignored so callers cannot
    /// delete controller-owned images accidentally.
    /// </summary>
    bool TryReleaseTemporaryImage(string path);
}
