using System;
using DrillFlow.Application.LiveInteraction;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationLiveImageCoordinateMapperTests
{
    [Fact]
    public void UniformImage_CentreMapsToZeroMoveAcrossDifferentDpiScale()
    {
        var hit = LiveImageCoordinateMapper.TryMapToRelativeMove(
            768,
            512,
            384d,
            256d,
            192d,
            128d,
            1E-5,
            1,
            1,
            out var target);

        Assert.True(hit);
        Assert.Equal(384d, target.SourceX, 12);
        Assert.Equal(256d, target.SourceY, 12);
        Assert.Equal(0d, target.MoveXMetres, 12);
        Assert.Equal(0d, target.MoveYMetres, 12);
    }

    [Fact]
    public void UniformImage_RejectsPointerInsideVerticalLetterbox()
    {
        var hit = LiveImageCoordinateMapper.TryMapToRelativeMove(
            768,
            512,
            900d,
            900d,
            450d,
            100d,
            1E-5,
            1,
            1,
            out _);

        Assert.False(hit);
    }

    [Fact]
    public void UniformImage_RemovesHorizontalLetterboxAndAppliesIndependentAxisSigns()
    {
        // 768x512 in 1200x600 renders as 900x600 with a 150 DIP bar on each side.
        const double scale = 600d / 512d;
        var clickX = 150d + 484d * scale;
        var clickY = 206d * scale;

        var hit = LiveImageCoordinateMapper.TryMapToRelativeMove(
            768,
            512,
            1200d,
            600d,
            clickX,
            clickY,
            1E-4,
            1,
            -1,
            out var target);

        Assert.True(hit);
        Assert.Equal(484d, target.SourceX, 10);
        Assert.Equal(206d, target.SourceY, 10);
        Assert.Equal(0.01d, target.MoveXMetres, 12);
        Assert.Equal(0.005d, target.MoveYMetres, 12);
    }

    [Fact]
    public void MappedMove_ReturnsDomainLimitAndLargerValuesForUiValidation()
    {
        Assert.True(LiveImageCoordinateMapper.TryMapToRelativeMove(
            1000,
            1000,
            1000d,
            1000d,
            1000d,
            500d,
            1E-3,
            1,
            1,
            out var boundary));
        Assert.Equal(0.5d, boundary.MoveXMetres, 12);

        Assert.True(LiveImageCoordinateMapper.TryMapToRelativeMove(
            1000,
            1000,
            1000d,
            1000d,
            1000d,
            500d,
            1.2E-3,
            1,
            1,
            out var outsideDomain));
        Assert.Equal(0.6d, outsideDomain.MoveXMetres, 12);
    }

    [Fact]
    public void MappedMove_RejectsNumericOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LiveImageCoordinateMapper.TryMapToRelativeMove(
                1000,
                1000,
                1000d,
                1000d,
                1000d,
                500d,
                double.MaxValue,
                1,
                1,
                out _));
    }

    [Fact]
    public void UniformImage_UsesAnisotropicBitmapDpiForLetterboxingAndPixelMapping()
    {
        // The bitmap's WPF natural size is 400x400 DIP: 800px at 192 DPI by 400px at 96 DPI.
        // It therefore fills this square viewport even though its raw pixel aspect ratio is 2:1.
        var hit = LiveImageCoordinateMapper.TryMapToRelativeMove(
            800,
            400,
            192d,
            96d,
            400d,
            400d,
            300d,
            200d,
            1E-5,
            1,
            1,
            out var target);

        Assert.True(hit);
        Assert.Equal(600d, target.SourceX, 10);
        Assert.Equal(200d, target.SourceY, 10);
        Assert.Equal(0.002d, target.MoveXMetres, 12);
        Assert.Equal(0d, target.MoveYMetres, 12);
    }

    [Fact]
    public void SourceNaturalSize_MapsRenderedPreviewBackToOriginalPixelCoordinates()
    {
        // The UI renders the decoded preview as a square even though physical-coordinate
        // reporting keeps the original 2:1 pixel dimensions. Hit testing must follow the
        // preview WPF actually rendered, then normalize back into the original pixel space.
        var hit = LiveImageCoordinateMapper.TryMapToRelativeMoveFromSourceNaturalSize(
            4000,
            2000,
            480d,
            480d,
            900d,
            600d,
            600d,
            150d,
            1E-6,
            1,
            -1,
            out var target);

        Assert.True(hit);
        Assert.Equal(3000d, target.SourceX, 12);
        Assert.Equal(500d, target.SourceY, 12);
        Assert.Equal(1E-3, target.MoveXMetres, 12);
        Assert.Equal(0.5E-3, target.MoveYMetres, 12);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void SourceNaturalSize_IsInvariantWhenAllRenderedGeometryUsesMonitorDpiScale(
        double monitorDpiScale)
    {
        const int sourceWidth = 4096;
        const int sourceHeight = 2160;
        const double naturalWidth = 960d;
        const double naturalHeight = 540d;
        const double viewportWidth = 1100d;
        const double viewportHeight = 800d;
        const double expectedSourceX = 3072d;
        const double expectedSourceY = 540d;
        var bounds = LiveImageCoordinateMapper.GetUniformRenderedBounds(
            naturalWidth,
            naturalHeight,
            viewportWidth,
            viewportHeight);
        var clickX = bounds.Left + expectedSourceX / sourceWidth * bounds.Width;
        var clickY = bounds.Top + expectedSourceY / sourceHeight * bounds.Height;

        var hit = LiveImageCoordinateMapper.TryMapToRelativeMoveFromSourceNaturalSize(
            sourceWidth,
            sourceHeight,
            naturalWidth * monitorDpiScale,
            naturalHeight * monitorDpiScale,
            viewportWidth * monitorDpiScale,
            viewportHeight * monitorDpiScale,
            clickX * monitorDpiScale,
            clickY * monitorDpiScale,
            2E-6,
            1,
            1,
            out var target);

        Assert.True(hit);
        Assert.Equal(expectedSourceX, target.SourceX, 10);
        Assert.Equal(expectedSourceY, target.SourceY, 10);
        Assert.Equal(0.002048d, target.MoveXMetres, 12);
        Assert.Equal(-0.00108d, target.MoveYMetres, 12);
    }

    [Fact]
    public void UniformRenderedBounds_SourcePointRoundTripsThroughLetterboxedViewport()
    {
        const int sourceWidth = 4096;
        const int sourceHeight = 2160;
        const double sourcePixelX = 1571.25d;
        const double sourcePixelY = 777.75d;
        var bounds = LiveImageCoordinateMapper.GetUniformRenderedBounds(
            640d,
            480d,
            1000d,
            500d);
        var viewportX = bounds.Left + sourcePixelX / sourceWidth * bounds.Width;
        var viewportY = bounds.Top + sourcePixelY / sourceHeight * bounds.Height;

        var hit = LiveImageCoordinateMapper.TryMapToRelativeMoveFromSourceNaturalSize(
            sourceWidth,
            sourceHeight,
            640d,
            480d,
            1000d,
            500d,
            viewportX,
            viewportY,
            1E-6,
            1,
            1,
            out var target);

        Assert.Equal(166.66666666666666d, bounds.Left, 10);
        Assert.Equal(0d, bounds.Top, 12);
        Assert.Equal(666.6666666666666d, bounds.Width, 10);
        Assert.Equal(500d, bounds.Height, 12);
        Assert.True(hit);
        Assert.Equal(sourcePixelX, target.SourceX, 10);
        Assert.Equal(sourcePixelY, target.SourceY, 10);
    }

    [Theory]
    [InlineData(0d, 1, 1)]
    [InlineData(double.NaN, 1, 1)]
    [InlineData(1E-5, 0, 1)]
    [InlineData(1E-5, 1, 2)]
    public void InvalidPitchOrAxisSigns_AreRejected(
        double pixelPitch,
        int xAxisSign,
        int yAxisSign)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LiveImageCoordinateMapper.TryMapToRelativeMove(
                768,
                512,
                768d,
                512d,
                384d,
                256d,
                pixelPitch,
                xAxisSign,
                yAxisSign,
                out _));
    }
}
