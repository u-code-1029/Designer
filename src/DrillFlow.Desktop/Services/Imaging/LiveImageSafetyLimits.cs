using System.IO;

namespace DrillFlow.Desktop.Services;

public static class LiveImageSafetyLimits
{
    public const long MaximumEncodedBytes = 64L * 1024L * 1024L;
    public const int MaximumPixelDimension = 16384;
    public const long MaximumPixelCount = 64_000_000L;

    public static void ValidateEncodedByteLength(long byteLength)
    {
        if (byteLength <= 0)
        {
            throw new InvalidDataException("The image file is empty.");
        }

        if (byteLength > MaximumEncodedBytes)
        {
            throw new LiveImageLimitExceededException(
                $"The image file is {byteLength} bytes; the safe limit is {MaximumEncodedBytes} bytes (64 MiB).");
        }
    }

    public static void ValidatePixelDimensions(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new InvalidDataException("The image has invalid pixel dimensions.");
        }

        var pixelCount = (long)pixelWidth * pixelHeight;
        if (pixelWidth > MaximumPixelDimension
            || pixelHeight > MaximumPixelDimension
            || pixelCount > MaximumPixelCount)
        {
            throw new LiveImageLimitExceededException(
                $"The image is {pixelWidth} x {pixelHeight} pixels ({pixelCount} pixels); "
                + $"the safe limits are {MaximumPixelDimension} pixels per axis and {MaximumPixelCount} total pixels.");
        }
    }
}
