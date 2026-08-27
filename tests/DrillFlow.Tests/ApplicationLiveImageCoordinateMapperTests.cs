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
