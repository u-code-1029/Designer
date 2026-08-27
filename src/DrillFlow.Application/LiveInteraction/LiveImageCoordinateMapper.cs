using System;

namespace DrillFlow.Application.LiveInteraction;

/// <summary>
/// Maps a pointer position from a WPF-style DIP viewport displaying an image with Uniform stretch
/// into source-image coordinates and a relative stage move. This type deliberately has no WPF
/// dependency, so letterboxing, DPI scaling, and axis orientation remain independently testable.
/// </summary>
public static class LiveImageCoordinateMapper
{
    public static bool TryMapToRelativeMove(
        int sourcePixelWidth,
        int sourcePixelHeight,
        double viewportWidthDip,
        double viewportHeightDip,
        double clickXDip,
        double clickYDip,
        double pixelPitchMetres,
        int xAxisSign,
        int yAxisSign,
        out LiveImageMoveTarget target)
    {
        return TryMapToRelativeMove(
            sourcePixelWidth,
            sourcePixelHeight,
            96d,
            96d,
            viewportWidthDip,
            viewportHeightDip,
            clickXDip,
            clickYDip,
            pixelPitchMetres,
            xAxisSign,
            yAxisSign,
            out target);
    }

    public static bool TryMapToRelativeMove(
        int sourcePixelWidth,
        int sourcePixelHeight,
        double sourceDpiX,
        double sourceDpiY,
        double viewportWidthDip,
        double viewportHeightDip,
        double clickXDip,
        double clickYDip,
        double pixelPitchMetres,
        int xAxisSign,
        int yAxisSign,
        out LiveImageMoveTarget target)
    {
        ValidatePositive(sourcePixelWidth, nameof(sourcePixelWidth));
        ValidatePositive(sourcePixelHeight, nameof(sourcePixelHeight));
        ValidatePositiveFinite(sourceDpiX, nameof(sourceDpiX));
        ValidatePositiveFinite(sourceDpiY, nameof(sourceDpiY));

        var naturalWidthDip = sourcePixelWidth * 96d / sourceDpiX;
        var naturalHeightDip = sourcePixelHeight * 96d / sourceDpiY;
        return TryMapToRelativeMoveFromSourceNaturalSize(
            sourcePixelWidth,
            sourcePixelHeight,
            naturalWidthDip,
            naturalHeightDip,
            viewportWidthDip,
            viewportHeightDip,
            clickXDip,
            clickYDip,
            pixelPitchMetres,
            xAxisSign,
            yAxisSign,
            out target);
    }

    /// <summary>
    /// Maps a point using the natural DIP size of the image source that WPF actually renders.
    /// This keeps hit testing exact when the preview bitmap was downsampled or its effective DPI
    /// differs from the original image metadata retained for physical-coordinate reporting.
    /// </summary>
    public static bool TryMapToRelativeMoveFromSourceNaturalSize(
        int sourcePixelWidth,
        int sourcePixelHeight,
        double sourceNaturalWidthDip,
        double sourceNaturalHeightDip,
        double viewportWidthDip,
        double viewportHeightDip,
        double clickXDip,
        double clickYDip,
        double pixelPitchMetres,
        int xAxisSign,
        int yAxisSign,
        out LiveImageMoveTarget target)
    {
        ValidatePositive(sourcePixelWidth, nameof(sourcePixelWidth));
        ValidatePositive(sourcePixelHeight, nameof(sourcePixelHeight));
        ValidatePositiveFinite(sourceNaturalWidthDip, nameof(sourceNaturalWidthDip));
        ValidatePositiveFinite(sourceNaturalHeightDip, nameof(sourceNaturalHeightDip));
        ValidatePositiveFinite(viewportWidthDip, nameof(viewportWidthDip));
        ValidatePositiveFinite(viewportHeightDip, nameof(viewportHeightDip));
        ValidateFinite(clickXDip, nameof(clickXDip));
        ValidateFinite(clickYDip, nameof(clickYDip));
        ValidatePositiveFinite(pixelPitchMetres, nameof(pixelPitchMetres));
        ValidateAxisSign(xAxisSign, nameof(xAxisSign));
        ValidateAxisSign(yAxisSign, nameof(yAxisSign));

        var bounds = GetUniformRenderedBounds(
            sourceNaturalWidthDip,
            sourceNaturalHeightDip,
            viewportWidthDip,
            viewportHeightDip);

        if (clickXDip < bounds.Left
            || clickXDip > bounds.Right
            || clickYDip < bounds.Top
            || clickYDip > bounds.Bottom)
        {
            target = default;
            return false;
        }

        // Source coordinates are continuous pixel-space coordinates. Consequently, the image
        // centre is exactly width/2,height/2 and the right/bottom outer edges are width,height.
        var sourceX = (clickXDip - bounds.Left) / bounds.Width * sourcePixelWidth;
        var sourceY = (clickYDip - bounds.Top) / bounds.Height * sourcePixelHeight;
        var moveX = (sourceX - sourcePixelWidth / 2d) * pixelPitchMetres * xAxisSign;
        var moveY = (sourceY - sourcePixelHeight / 2d) * pixelPitchMetres * yAxisSign;

        if (double.IsNaN(moveX)
            || double.IsInfinity(moveX)
            || double.IsNaN(moveY)
            || double.IsInfinity(moveY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pixelPitchMetres),
                "The mapped stage move must remain finite.");
        }

        target = new LiveImageMoveTarget(sourceX, sourceY, moveX, moveY);
        return true;
    }

    /// <summary>Returns the exact Uniform-stretch content rectangle in viewport DIPs.</summary>
    public static LiveImageRenderedBounds GetUniformRenderedBounds(
        double sourceNaturalWidthDip,
        double sourceNaturalHeightDip,
        double viewportWidthDip,
        double viewportHeightDip)
    {
        ValidatePositiveFinite(sourceNaturalWidthDip, nameof(sourceNaturalWidthDip));
        ValidatePositiveFinite(sourceNaturalHeightDip, nameof(sourceNaturalHeightDip));
        ValidatePositiveFinite(viewportWidthDip, nameof(viewportWidthDip));
        ValidatePositiveFinite(viewportHeightDip, nameof(viewportHeightDip));

        var scale = Math.Min(
            viewportWidthDip / sourceNaturalWidthDip,
            viewportHeightDip / sourceNaturalHeightDip);
        var renderedWidth = sourceNaturalWidthDip * scale;
        var renderedHeight = sourceNaturalHeightDip * scale;
        return new LiveImageRenderedBounds(
            (viewportWidthDip - renderedWidth) / 2d,
            (viewportHeightDip - renderedHeight) / 2d,
            renderedWidth,
            renderedHeight);
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");
        }
    }

    private static void ValidatePositiveFinite(double value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be finite.");
        }
    }

    private static void ValidateAxisSign(int value, string parameterName)
    {
        if (value != -1 && value != 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "An axis sign must be -1 or 1.");
        }
    }
}

/// <summary>The image point and stage offset calculated for one pointer hit.</summary>
public readonly struct LiveImageMoveTarget
{
    public LiveImageMoveTarget(
        double sourceX,
        double sourceY,
        double moveXMetres,
        double moveYMetres)
    {
        SourceX = sourceX;
        SourceY = sourceY;
        MoveXMetres = moveXMetres;
        MoveYMetres = moveYMetres;
    }

    public double SourceX { get; }

    public double SourceY { get; }

    public double MoveXMetres { get; }

    public double MoveYMetres { get; }
}

/// <summary>The Uniform-stretch image content rectangle inside a viewport, in WPF DIPs.</summary>
public readonly struct LiveImageRenderedBounds
{
    public LiveImageRenderedBounds(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => Left + Width;

    public double Bottom => Top + Height;
}
