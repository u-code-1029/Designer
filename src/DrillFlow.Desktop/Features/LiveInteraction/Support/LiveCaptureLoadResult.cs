using System;
using DrillFlow.Desktop.Services;

namespace DrillFlow.Desktop.ViewModels;

internal sealed class LiveCaptureLoadResult
{
    public LiveCaptureLoadResult(
        LiveCaptureSnapshot snapshot,
        LiveImageDecodeResult image)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    public LiveCaptureSnapshot Snapshot { get; }

    public LiveImageDecodeResult Image { get; }
}
