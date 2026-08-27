using System;
using DrillFlow.Desktop.Controls;
using DrillFlow.Desktop.ViewModels;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopFocusScatterChartTests
{
    [Fact]
    public void TryCreate_EmptyInputDoesNotCreateScale()
    {
        Assert.False(FocusChartScale.TryCreate(null, out _));
        Assert.False(
            FocusChartScale.TryCreate(Array.Empty<FocusSamplePoint>(), out _));
    }

    [Fact]
    public void TryCreate_NormalSamplesAddsFivePercentPaddingToBothAxes()
    {
        var points = new[]
        {
            new FocusSamplePoint(1d, 10d),
            new FocusSamplePoint(3d, 50d),
            new FocusSamplePoint(2d, 30d),
        };

        Assert.True(FocusChartScale.TryCreate(points, out var scale));

        Assert.Equal(0.9d, scale.MinimumX, 12);
        Assert.Equal(3.1d, scale.MaximumX, 12);
        Assert.Equal(8d, scale.MinimumY, 12);
        Assert.Equal(52d, scale.MaximumY, 12);
        Assert.Equal(2.2d, scale.RangeX, 12);
        Assert.Equal(44d, scale.RangeY, 12);
    }

    [Fact]
    public void TryCreate_SingleSampleExpandsDegenerateAxesAroundValue()
    {
        var point = new FocusSamplePoint(2E-6d, 500d);

        Assert.True(FocusChartScale.TryCreate(new[] { point }, out var scale));

        Assert.Equal(1.9E-6d, scale.MinimumX, 15);
        Assert.Equal(2.1E-6d, scale.MaximumX, 15);
        Assert.Equal(475d, scale.MinimumY, 12);
        Assert.Equal(525d, scale.MaximumY, 12);
        Assert.True(scale.MinimumX < point.ZMetres);
        Assert.True(scale.MaximumX > point.ZMetres);
        Assert.True(scale.MinimumY < point.Sharpness);
        Assert.True(scale.MaximumY > point.Sharpness);
    }

    [Fact]
    public void TryCreate_WideScientificRangeRemainsFiniteAndContainsEverySample()
    {
        var points = new[]
        {
            new FocusSamplePoint(1E-12d, 1E3d),
            new FocusSamplePoint(2.4E-3d, 1E12d),
            new FocusSamplePoint(7.5E-7d, 8.2E8d),
        };

        Assert.True(FocusChartScale.TryCreate(points, out var scale));

        Assert.True(FocusChartScale.IsFinite(scale.MinimumX));
        Assert.True(FocusChartScale.IsFinite(scale.MaximumX));
        Assert.True(FocusChartScale.IsFinite(scale.MinimumY));
        Assert.True(FocusChartScale.IsFinite(scale.MaximumY));
        Assert.True(scale.RangeX > 0d);
        Assert.True(scale.RangeY > 0d);
        foreach (var point in points)
        {
            Assert.InRange(point.ZMetres, scale.MinimumX, scale.MaximumX);
            Assert.InRange(point.Sharpness, scale.MinimumY, scale.MaximumY);
        }

        var expectedXPadding = (2.4E-3d - 1E-12d) * 0.05d;
        var expectedYPadding = (1E12d - 1E3d) * 0.05d;
        Assert.Equal(1E-12d - expectedXPadding, scale.MinimumX, 15);
        Assert.Equal(2.4E-3d + expectedXPadding, scale.MaximumX, 15);
        Assert.Equal(1E3d - expectedYPadding, scale.MinimumY, 6);
        Assert.Equal(1E12d + expectedYPadding, scale.MaximumY, 6);
    }

    [Fact]
    public void TryCreate_MaximumFiniteValuesDoNotOverflowAxisBounds()
    {
        var points = new[]
        {
            new FocusSamplePoint(double.MaxValue * 0.9d, double.MaxValue * 0.8d),
            new FocusSamplePoint(double.MaxValue, double.MaxValue)
        };

        Assert.True(FocusChartScale.TryCreate(points, out var scale));
        Assert.True(FocusChartScale.IsFinite(scale.MinimumX));
        Assert.True(FocusChartScale.IsFinite(scale.MaximumX));
        Assert.True(FocusChartScale.IsFinite(scale.MinimumY));
        Assert.True(FocusChartScale.IsFinite(scale.MaximumY));
        Assert.True(scale.RangeX > 0d);
        Assert.True(scale.RangeY > 0d);
        Assert.Equal(double.MaxValue, scale.MaximumX);
        Assert.Equal(double.MaxValue, scale.MaximumY);
    }
}
