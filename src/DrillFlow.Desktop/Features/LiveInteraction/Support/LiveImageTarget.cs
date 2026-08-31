namespace DrillFlow.Desktop.ViewModels;

public sealed class LiveImageTarget
{
    public LiveImageTarget(
        double pixelX,
        double pixelY,
        int imagePixelWidth,
        int imagePixelHeight,
        double moveXMetres,
        double moveYMetres)
    {
        PixelX = pixelX;
        PixelY = pixelY;
        ImagePixelWidth = imagePixelWidth;
        ImagePixelHeight = imagePixelHeight;
        MoveXMetres = moveXMetres;
        MoveYMetres = moveYMetres;
    }

    public double PixelX { get; }

    public double PixelY { get; }

    public int ImagePixelWidth { get; }

    public int ImagePixelHeight { get; }

    public double MoveXMetres { get; }

    public double MoveYMetres { get; }
}
