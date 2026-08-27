using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Views;

public partial class LiveInteractionPage : Page
{
    private Point? _contextMenuPoint;
    private LiveImageTarget? _contextMoveTarget;

    public LiveInteractionPage(LiveInteractionPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private LiveInteractionPageViewModel ViewModel => (LiveInteractionPageViewModel)DataContext;

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Activate();
        UpdateTargetMarker();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Deactivate();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
        {
            return;
        }

        var zoomIn = (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Add)
                     || (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.OemPlus);
        var zoomOut = Keyboard.Modifiers == ModifierKeys.None
                      && (e.Key == Key.Subtract || e.Key == Key.OemMinus);
        var command = zoomIn
            ? ViewModel.ZoomFrameInCommand
            : zoomOut
                ? ViewModel.ZoomFrameOutCommand
                : null;
        if (command?.CanExecute(null) != true)
        {
            return;
        }

        command.Execute(null);
        e.Handled = true;
    }

    private void ImageViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var command = e.Delta > 0
            ? ViewModel.ZoomFrameInCommand
            : e.Delta < 0
                ? ViewModel.ZoomFrameOutCommand
                : null;
        if (command?.CanExecute(null) != true)
        {
            return;
        }

        command.Execute(null);
        e.Handled = true;
    }

    private void LiveImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2 || !ViewModel.HasImage)
        {
            return;
        }

        var point = e.GetPosition(LiveImage);
        if (!TryCreateMoveTarget(point, out var target)
            || target is null
            || !ViewModel.MoveToTargetCommand.CanExecute(target))
        {
            return;
        }

        ViewModel.MoveToTargetCommand.Execute(target);
        e.Handled = true;
    }

    private void LiveImage_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextMenuPoint = e.GetPosition(LiveImage);
    }

    private void LiveImage_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var point = GetContextMenuPoint();
        _contextMoveTarget = TryCreateMoveTarget(point, out var target)
            ? target
            : null;
        MoveHereMenuItem.IsEnabled = _contextMoveTarget is not null
                                     && ViewModel.MoveToTargetCommand.CanExecute(
                                         _contextMoveTarget);
    }

    private void MoveHereMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // A live frame may have changed dimensions while the context menu was open. Re-map the
        // stored viewport point against the current image and calibration immediately before the
        // command so an older preview cannot produce a stale physical displacement.
        var target = TryCreateMoveTarget(GetContextMenuPoint(), out var currentTarget)
            ? currentTarget
            : null;
        if (target is null || !ViewModel.MoveToTargetCommand.CanExecute(target))
        {
            return;
        }

        ViewModel.MoveToTargetCommand.Execute(target);
        e.Handled = true;
    }

    private void LiveImageContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _contextMenuPoint = null;
        _contextMoveTarget = null;
    }

    private Point GetContextMenuPoint()
    {
        return _contextMenuPoint
               ?? new Point(LiveImage.ActualWidth / 2d, LiveImage.ActualHeight / 2d);
    }

    private bool TryCreateMoveTarget(Point point, out LiveImageTarget? target)
    {
        target = null;
        return ViewModel.HasImage
               && TryGetSourceNaturalSize(out var sourceWidthDip, out var sourceHeightDip)
               && ViewModel.TryCreateMoveTarget(
                   sourceWidthDip,
                   sourceHeightDip,
                   LiveImage.ActualWidth,
                   LiveImage.ActualHeight,
                   point.X,
                   point.Y,
                   out target);
    }

    private void LiveImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTargetMarker();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveInteractionPageViewModel.HasTarget)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.IsTargetMarkerVisible)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.TargetPixelX)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.TargetPixelY)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.ImagePixelWidth)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.ImagePixelHeight)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.ImageDpiX)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.ImageDpiY)
            || e.PropertyName == nameof(LiveInteractionPageViewModel.LiveImageSource))
        {
            Dispatcher.BeginInvoke(new Action(UpdateTargetMarker));
        }
    }

    private void UpdateTargetMarker()
    {
        if (!ViewModel.IsTargetMarkerVisible
            || ViewModel.ImagePixelWidth <= 0
            || ViewModel.ImagePixelHeight <= 0
            || LiveImage.ActualWidth <= 0
            || LiveImage.ActualHeight <= 0
            || !TryGetSourceNaturalSize(out var sourceWidthDip, out var sourceHeightDip))
        {
            SetTargetVisibility(Visibility.Collapsed);
            return;
        }

        var bounds = LiveImageCoordinateMapper.GetUniformRenderedBounds(
            sourceWidthDip,
            sourceHeightDip,
            LiveImage.ActualWidth,
            LiveImage.ActualHeight);
        var x = bounds.Left
                + ViewModel.TargetPixelX / ViewModel.ImagePixelWidth * bounds.Width;
        var y = bounds.Top
                + ViewModel.TargetPixelY / ViewModel.ImagePixelHeight * bounds.Height;

        Canvas.SetLeft(TargetCircle, x - TargetCircle.Width / 2d);
        Canvas.SetTop(TargetCircle, y - TargetCircle.Height / 2d);
        TargetHorizontal.X1 = x - 18d;
        TargetHorizontal.X2 = x + 18d;
        TargetHorizontal.Y1 = y;
        TargetHorizontal.Y2 = y;
        TargetVertical.X1 = x;
        TargetVertical.X2 = x;
        TargetVertical.Y1 = y - 18d;
        TargetVertical.Y2 = y + 18d;
        SetTargetVisibility(Visibility.Visible);
    }

    private bool TryGetSourceNaturalSize(out double widthDip, out double heightDip)
    {
        var source = LiveImage.Source;
        widthDip = source?.Width ?? 0d;
        heightDip = source?.Height ?? 0d;
        if (IsPositiveFinite(widthDip) && IsPositiveFinite(heightDip))
        {
            return true;
        }

        widthDip = ViewModel.ImageDpiX > 0d
            ? ViewModel.ImagePixelWidth * 96d / ViewModel.ImageDpiX
            : 0d;
        heightDip = ViewModel.ImageDpiY > 0d
            ? ViewModel.ImagePixelHeight * 96d / ViewModel.ImageDpiY
            : 0d;
        return IsPositiveFinite(widthDip) && IsPositiveFinite(heightDip);
    }

    private static bool IsPositiveFinite(double value) =>
        value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);

    private void SetTargetVisibility(Visibility visibility)
    {
        TargetCircle.Visibility = visibility;
        TargetHorizontal.Visibility = visibility;
        TargetVertical.Visibility = visibility;
    }
}
