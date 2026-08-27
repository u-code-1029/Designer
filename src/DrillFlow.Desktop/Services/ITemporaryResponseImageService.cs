using System;
using System.Linq;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// A generated response image whose frozen bitmap is safe to retain in the dialog even though the
/// PNG file is not kept open.
/// </summary>
public sealed class TemporaryResponseImage
{
    public TemporaryResponseImage(string path, BitmapSource imageSource)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        ImageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
    }

    public string Path { get; }

    public BitmapSource ImageSource { get; }
}

public sealed class TemporaryResponseImageService : ITemporaryResponseImageService, IDisposable
{
    public const int ImageWidth = 768;
    public const int ImageHeight = 512;

    private const string TemporaryFilePrefix = "response-";
    private const string TemporaryFilePattern = TemporaryFilePrefix + "*.png";

    private readonly object _sync = new object();
    private readonly System.Collections.Generic.HashSet<string> _createdPaths
        = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TemporaryResponseImageService> _logger;
    private readonly string _temporaryDirectory;
    private bool _disposed;

    public TemporaryResponseImageService(
        ILogger<TemporaryResponseImageService> logger)
        : this(
            logger,
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrillFlow",
                "TemporaryResponseImages"))
    {
    }

    internal TemporaryResponseImageService(
        ILogger<TemporaryResponseImageService> logger,
        string temporaryDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            throw new ArgumentException("A temporary image directory is required.", nameof(temporaryDirectory));
        }

        _temporaryDirectory = System.IO.Path.GetFullPath(temporaryDirectory);

        DeleteOrphanedTemporaryFiles();
    }

    public TemporaryResponseImage CreateTemporaryImage()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            System.IO.Directory.CreateDirectory(_temporaryDirectory);

            var path = System.IO.Path.Combine(
                _temporaryDirectory,
                TemporaryFilePrefix + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var bitmap = CreateMosaicBitmap();
                SavePng(path, bitmap);
                _createdPaths.Add(path);
                return new TemporaryResponseImage(path, bitmap);
            }
            catch
            {
                TryDeleteFile(path, "failed image generation");
                TryDeleteDirectoryIfEmpty();
                throw;
            }
        }
    }

    public bool TryReleaseTemporaryImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath = System.IO.Path.GetFullPath(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is NotSupportedException
                || exception is System.Security.SecurityException)
            {
                return false;
            }

            if (!_createdPaths.Contains(normalizedPath))
            {
                return false;
            }

            if (!TryDeleteFile(normalizedPath, "live frame replacement"))
            {
                return false;
            }

            _createdPaths.Remove(normalizedPath);
            TryDeleteDirectoryIfEmpty();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var path in _createdPaths.ToArray())
            {
                TryDeleteFile(path, "application shutdown");
            }

            _createdPaths.Clear();
            TryDeleteDirectoryIfEmpty();
        }
    }

    private static BitmapSource CreateMosaicBitmap()
    {
        const int bytesPerPixel = 4;
        const int tileSize = 64;
        var stride = ImageWidth * bytesPerPixel;
        var pixels = new byte[stride * ImageHeight];
        var tilesAcross = (ImageWidth + tileSize - 1) / tileSize;
        var tilesDown = (ImageHeight + tileSize - 1) / tileSize;
        var tileColors = new byte[tilesAcross * tilesDown * 3];
        using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            random.GetBytes(tileColors);
        }

        var hasPreviousColor = false;
        byte previousBlue = 0;
        byte previousGreen = 0;
        byte previousRed = 0;
        for (var tileY = 0; tileY < tilesDown; tileY++)
        {
            for (var tileX = 0; tileX < tilesAcross; tileX++)
            {
                var colorIndex = ((tileY * tilesAcross) + tileX) * 3;
                var blue = NormalizeTileColor(tileColors[colorIndex]);
                var green = NormalizeTileColor(tileColors[colorIndex + 1]);
                var red = NormalizeTileColor(tileColors[colorIndex + 2]);
                if (hasPreviousColor
                    && blue == previousBlue
                    && green == previousGreen
                    && red == previousRed)
                {
                    red = red == 215 ? (byte)40 : (byte)(red + 1);
                }

                previousBlue = blue;
                previousGreen = green;
                previousRed = red;
                hasPreviousColor = true;

                var startX = tileX * tileSize;
                var startY = tileY * tileSize;
                var endX = Math.Min(startX + tileSize, ImageWidth);
                var endY = Math.Min(startY + tileSize, ImageHeight);
                for (var y = startY; y < endY; y++)
                {
                    var isHorizontalGrout = y == startY || y == endY - 1;
                    for (var x = startX; x < endX; x++)
                    {
                        var isGrout = isHorizontalGrout || x == startX || x == endX - 1;
                        var pixelIndex = (y * stride) + (x * bytesPerPixel);
                        pixels[pixelIndex] = isGrout ? Darken(blue) : blue;
                        pixels[pixelIndex + 1] = isGrout ? Darken(green) : green;
                        pixels[pixelIndex + 2] = isGrout ? Darken(red) : red;
                        pixels[pixelIndex + 3] = byte.MaxValue;
                    }
                }
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            ImageWidth,
            ImageHeight,
            96d,
            96d,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte NormalizeTileColor(byte value)
    {
        // Avoid almost-black and almost-white tiles so every regenerated preview is immediately
        // legible in both light and dark application themes.
        return (byte)(40 + (value % 176));
    }

    private static byte Darken(byte value)
    {
        return (byte)(value * 0.62d);
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = new System.IO.FileStream(
                   path,
                   System.IO.FileMode.CreateNew,
                   System.IO.FileAccess.Write,
                   System.IO.FileShare.None))
        {
            encoder.Save(stream);
            stream.Flush(true);
        }
    }

    private void DeleteOrphanedTemporaryFiles()
    {
        string[] paths;
        try
        {
            paths = System.IO.Directory.Exists(_temporaryDirectory)
                ? System.IO.Directory.GetFiles(
                    _temporaryDirectory,
                    TemporaryFilePattern,
                    System.IO.SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            _logger.LogWarning(
                exception,
                "Could not enumerate temporary response images during {CleanupReason}.",
                "application startup");
            return;
        }

        foreach (var path in paths)
        {
            TryDeleteFile(path, "application startup");
        }

        TryDeleteDirectoryIfEmpty();
    }

    private bool TryDeleteFile(string path, string reason)
    {
        try
        {
            System.IO.File.Delete(path);
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            _logger.LogWarning(
                exception,
                "Could not delete temporary response image {ImagePath} during {CleanupReason}.",
                path,
                reason);
            return false;
        }
    }

    private void TryDeleteDirectoryIfEmpty()
    {
        try
        {
            if (System.IO.Directory.Exists(_temporaryDirectory)
                && !System.IO.Directory.EnumerateFileSystemEntries(_temporaryDirectory).Any())
            {
                System.IO.Directory.Delete(_temporaryDirectory, false);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            _logger.LogDebug(
                exception,
                "Could not remove the temporary response image directory {ImageDirectory}.",
                _temporaryDirectory);
        }
    }

    private static bool IsExpectedFileSystemException(Exception exception)
    {
        return exception is System.IO.IOException
               || exception is UnauthorizedAccessException
               || exception is System.Security.SecurityException;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TemporaryResponseImageService));
        }
    }
}
