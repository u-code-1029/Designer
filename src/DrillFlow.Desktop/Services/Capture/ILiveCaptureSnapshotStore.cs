using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Desktop.Services;

/// <summary>
/// Takes ownership of an equipment-produced capture before it can be replaced or deleted.
/// Snapshots contain the source bytes without transcoding.
/// </summary>
public interface ILiveCaptureSnapshotStore
{
    Task<LiveCaptureSnapshot> AcquireAsync(
        string sourceImagePath,
        CancellationToken cancellationToken);
}
