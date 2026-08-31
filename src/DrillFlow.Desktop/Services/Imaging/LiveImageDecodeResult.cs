using System;
using System.Windows.Media;

namespace DrillFlow.Desktop.Services;

public sealed class LiveImageDecodeResult
{
    public LiveImageDecodeResult(
        ImageSource imageSource,
        int originalPixelWidth,
        int originalPixelHeight,
        double originalDpiX,
        double originalDpiY,
        string detectedFileExtension)
    {
        ImageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
        OriginalPixelWidth = originalPixelWidth;
        OriginalPixelHeight = originalPixelHeight;
        OriginalDpiX = originalDpiX;
        OriginalDpiY = originalDpiY;
        DetectedFileExtension = detectedFileExtension ?? throw new ArgumentNullException(nameof(detectedFileExtension));
    }

    public ImageSource ImageSource { get; }

    public int OriginalPixelWidth { get; }

    public int OriginalPixelHeight { get; }

    public double OriginalDpiX { get; }

    public double OriginalDpiY { get; }

    public string DetectedFileExtension { get; }
}
