using System;
using System.Windows.Media.Imaging;

namespace DrillFlow.Desktop.Services;

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
