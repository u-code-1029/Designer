using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Controls;

/// <summary>A lightweight, theme-aware scatter chart for Focus Z/sharpness samples.</summary>
public sealed class FocusScatterChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<FocusSamplePoint>),
        typeof(FocusScatterChart),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush),
        typeof(Brush),
        typeof(FocusScatterChart),
        BrushMetadata(Brushes.Gray));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush),
        typeof(Brush),
        typeof(FocusScatterChart),
        BrushMetadata(Brushes.LightGray));

    public static readonly DependencyProperty PointBrushProperty = DependencyProperty.Register(
        nameof(PointBrush),
        typeof(Brush),
        typeof(FocusScatterChart),
        BrushMetadata(Brushes.DodgerBlue));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush),
        typeof(Brush),
        typeof(FocusScatterChart),
        BrushMetadata(Brushes.Gray));

    public FocusScatterChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = false;
    }

    public IReadOnlyList<FocusSamplePoint>? Points
    {
        get => (IReadOnlyList<FocusSamplePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush PointBrush
    {
        get => (Brush)GetValue(PointBrushProperty);
        set => SetValue(PointBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 120d || ActualHeight < 100d)
        {
            return;
        }

        var plot = new Rect(62d, 14d, ActualWidth - 78d, ActualHeight - 54d);
        if (plot.Width <= 0d || plot.Height <= 0d)
        {
            return;
        }

        var axisPen = CreatePen(AxisBrush, 1d);
        var gridPen = CreatePen(GridBrush, 1d);
        const int divisionCount = 4;
        for (var i = 0; i <= divisionCount; i++)
        {
            var ratio = i / (double)divisionCount;
            var x = plot.Left + plot.Width * ratio;
            var y = plot.Bottom - plot.Height * ratio;
            drawingContext.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        drawingContext.DrawLine(axisPen, plot.BottomLeft, plot.BottomRight);
        drawingContext.DrawLine(axisPen, plot.BottomLeft, plot.TopLeft);

        if (!FocusChartScale.TryCreate(Points, out var scale))
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            new FontFamily("Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        for (var i = 0; i <= divisionCount; i++)
        {
            var ratio = i / (double)divisionCount;
            var xValue = scale.MinimumX + scale.RangeX * ratio;
            var yValue = scale.MinimumY + scale.RangeY * ratio;
            var xLabel = CreateLabel(FormatAxisValue(xValue), typeface, pixelsPerDip);
            var yLabel = CreateLabel(FormatAxisValue(yValue), typeface, pixelsPerDip);
            var x = plot.Left + plot.Width * ratio;
            var y = plot.Bottom - plot.Height * ratio;
            drawingContext.DrawText(
                xLabel,
                new Point(x - xLabel.Width / 2d, plot.Bottom + 6d));
            drawingContext.DrawText(
                yLabel,
                new Point(plot.Left - yLabel.Width - 7d, y - yLabel.Height / 2d));
        }

        drawingContext.PushClip(new RectangleGeometry(plot));
        foreach (var point in Points!)
        {
            if (!FocusChartScale.IsFinite(point.ZMetres)
                || !FocusChartScale.IsFinite(point.Sharpness))
            {
                continue;
            }

            var x = plot.Left + (point.ZMetres - scale.MinimumX) / scale.RangeX * plot.Width;
            var y = plot.Bottom - (point.Sharpness - scale.MinimumY) / scale.RangeY * plot.Height;
            drawingContext.DrawEllipse(PointBrush, null, new Point(x, y), 4d, 4d);
        }

        drawingContext.Pop();
    }

    private static FrameworkPropertyMetadata BrushMetadata(Brush defaultBrush) =>
        new(defaultBrush, FrameworkPropertyMetadataOptions.AffectsRender);

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private FormattedText CreateLabel(
        string text,
        Typeface typeface,
        double pixelsPerDip) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            10d,
            TextBrush,
            pixelsPerDip);

    private static string FormatAxisValue(double value) =>
        value.ToString("0.##E+0", CultureInfo.CurrentCulture);
}

internal readonly struct FocusChartScale
{
    private FocusChartScale(double minimumX, double maximumX, double minimumY, double maximumY)
    {
        MinimumX = minimumX;
        MaximumX = maximumX;
        MinimumY = minimumY;
        MaximumY = maximumY;
    }

    public double MinimumX { get; }

    public double MaximumX { get; }

    public double MinimumY { get; }

    public double MaximumY { get; }

    public double RangeX => MaximumX - MinimumX;

    public double RangeY => MaximumY - MinimumY;

    public static bool TryCreate(
        IReadOnlyList<FocusSamplePoint>? points,
        out FocusChartScale scale)
    {
        var hasValue = false;
        var minimumX = double.PositiveInfinity;
        var maximumX = double.NegativeInfinity;
        var minimumY = double.PositiveInfinity;
        var maximumY = double.NegativeInfinity;
        if (points is not null)
        {
            foreach (var point in points)
            {
                if (!IsFinite(point.ZMetres) || !IsFinite(point.Sharpness))
                {
                    continue;
                }

                hasValue = true;
                minimumX = Math.Min(minimumX, point.ZMetres);
                maximumX = Math.Max(maximumX, point.ZMetres);
                minimumY = Math.Min(minimumY, point.Sharpness);
                maximumY = Math.Max(maximumY, point.Sharpness);
            }
        }

        if (!hasValue)
        {
            scale = default;
            return false;
        }

        ExpandRange(ref minimumX, ref maximumX);
        ExpandRange(ref minimumY, ref maximumY);
        scale = new FocusChartScale(minimumX, maximumX, minimumY, maximumY);
        return true;
    }

    internal static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static void ExpandRange(ref double minimum, ref double maximum)
    {
        var span = maximum - minimum;
        var padding = span > 0d
            ? span * 0.05d
            : Math.Max(Math.Abs(minimum) * 0.05d, 1E-12d);
        var expandedMinimum = minimum - padding;
        var expandedMaximum = maximum + padding;
        if (!IsFinite(expandedMinimum))
        {
            expandedMinimum = minimum >= 0d ? 0d : -double.MaxValue;
        }

        var maximumOverflowed = !IsFinite(expandedMaximum);
        if (maximumOverflowed)
        {
            expandedMaximum = double.MaxValue;
        }

        if (maximumOverflowed && minimum >= 0d && expandedMinimum < 0d)
        {
            expandedMinimum = 0d;
        }

        minimum = expandedMinimum;
        maximum = expandedMaximum;
    }
}
