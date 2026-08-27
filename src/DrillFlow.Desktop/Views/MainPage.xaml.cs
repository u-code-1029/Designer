using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Views;

public partial class MainPage : Page
{
    private const string DragFormat = "DrillFlow.WorkflowDragPayload";
    private const double MinimumCanvasZoom = 0.6;
    private const double MaximumCanvasZoom = 1.6;
    private const double CanvasZoomStep = 0.1;
    private const double InsertionMarkerRestingHeight = 28;
    private const double InsertionMarkerActiveHeight = 32;
    private Point _dragStart;
    private readonly Dictionary<Border, long> _insertionFlashVersions = new();
    private FrameworkElement? _actionDragSource;
    private WorkflowActionViewModel? _actionBeingDragged;
    private ModifierKeys _actionDragModifiers;
    private bool _actionWasSelectedOnMouseDown;
    private double _canvasZoom = 1.0;
    private long _nextInsertionFlashVersion;

    public MainPage(MainPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainPageViewModel ViewModel => (MainPageViewModel)DataContext;

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        SetCanvasZoom(_canvasZoom - CanvasZoomStep);

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => SetCanvasZoom(1.0);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        SetCanvasZoom(_canvasZoom + CanvasZoomStep);

    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        ToolboxColumn.Width = new GridLength(230);
        WorkflowColumn.Width = new GridLength(1, GridUnitType.Star);
        InspectorColumn.Width = new GridLength(380);
        EquipmentToolboxRow.Height = new GridLength(1, GridUnitType.Star);
        FlowToolboxRow.Height = new GridLength(1, GridUnitType.Star);

        EquipmentToolboxScrollViewer.ScrollToHome();
        FlowToolboxScrollViewer.ScrollToHome();
        WorkflowCanvasScrollViewer.ScrollToHome();
        ParameterScrollViewer.ScrollToHome();
        ResultScrollViewer.ScrollToHome();
        ImageScrollViewer.ScrollToHome();
        InspectorTabControl.SelectedIndex = 0;
        SetCanvasZoom(1.0);
    }

    private void SetCanvasZoom(double zoom)
    {
        _canvasZoom = Math.Max(
            MinimumCanvasZoom,
            Math.Min(MaximumCanvasZoom, Math.Round(zoom, 1)));
        WorkflowCanvasScaleTransform.ScaleX = _canvasZoom;
        WorkflowCanvasScaleTransform.ScaleY = _canvasZoom;
        CanvasZoomText.Text = $"{_canvasZoom:P0}";
    }

    private void ToolboxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.IsWorkflowEditingEnabled)
        {
            return;
        }

        _dragStart = e.GetPosition(this);
    }

    private void ToolboxItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!ViewModel.IsWorkflowEditingEnabled
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element
            || element.DataContext is not ToolboxItemViewModel item)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(DragFormat, new WorkflowDragPayload(item.Kind));
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);
    }

    private void ToolboxItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsWorkflowEditingEnabled
            && e.ClickCount == 2
            && sender is FrameworkElement element
            && element.DataContext is ToolboxItemViewModel item)
        {
            ViewModel.AddToolboxItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void ActionCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element
            && element.DataContext is WorkflowActionViewModel action
            && !IsInteractiveOrigin(e.OriginalSource as DependencyObject)
            && FindNearestAction(e.OriginalSource as DependencyObject) == action)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                ViewModel.SelectActionRange(action);
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ViewModel.ToggleActionSelection(action);
            }
            else
            {
                ViewModel.SelectAction(action);
            }

            element.Focus();
            e.Handled = true;
        }
    }

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsWorkflowEditingEnabled
            && sender is FrameworkElement element
            && element.DataContext is WorkflowActionViewModel action
            && FindNearestAction(e.OriginalSource as DependencyObject) == action)
        {
            ClearPendingActionDrag();
            _dragStart = e.GetPosition(this);
            _actionDragSource = element;
            _actionBeingDragged = action;
            _actionDragModifiers = Keyboard.Modifiers;
            _actionWasSelectedOnMouseDown = action.IsSelected;

            if ((_actionDragModifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                ViewModel.SelectActionRange(action);
            }
            else if ((_actionDragModifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (!_actionWasSelectedOnMouseDown)
                {
                    ViewModel.ToggleActionSelection(action);
                }
            }
            else
            {
                ViewModel.EnsureActionSelected(action);
            }

            element.Focus();
            Mouse.Capture(element);
            e.Handled = true;
        }
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element
            || element.DataContext is not WorkflowActionViewModel action
            || !ReferenceEquals(element, _actionDragSource)
            || !ReferenceEquals(action, _actionBeingDragged))
        {
            return;
        }

        if (!ViewModel.IsWorkflowEditingEnabled || e.LeftButton != MouseButtonState.Pressed)
        {
            ClearPendingActionDrag();
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var copyRequested = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var data = new DataObject(
            DragFormat,
            new WorkflowDragPayload(ViewModel.GetDragActions(action), copyRequested));
        ClearPendingActionDrag();
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Move);
        e.Handled = true;
    }

    private void DragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, _actionDragSource) && _actionBeingDragged is { } action)
        {
            var modifiers = _actionDragModifiers;
            var wasSelected = _actionWasSelectedOnMouseDown;
            ClearPendingActionDrag();

            if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (wasSelected)
                {
                    ViewModel.ToggleActionSelection(action);
                }
            }
            else if ((modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                ViewModel.SelectAction(action);
            }

            e.Handled = true;
        }
    }

    private void ClearPendingActionDrag()
    {
        var source = _actionDragSource;
        _actionDragSource = null;
        _actionBeingDragged = null;
        _actionDragModifiers = ModifierKeys.None;
        _actionWasSelectedOnMouseDown = false;
        if (source?.IsMouseCaptured == true)
        {
            source.ReleaseMouseCapture();
        }
    }

    private void ActionContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu
            || menu.PlacementTarget is not FrameworkElement target
            || target.DataContext is not WorkflowActionViewModel action)
        {
            return;
        }

        ViewModel.EnsureActionSelected(action);
    }

    private static WorkflowActionViewModel? FindNearestAction(DependencyObject? origin)
    {
        var current = origin;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is WorkflowActionViewModel action)
            {
                return action;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static bool IsInteractiveOrigin(DependencyObject? origin)
    {
        var current = origin;
        while (current is not null)
        {
            if (current is TextBoxBase
                or PasswordBox
                or Selector
                or ButtonBase)
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is ContentElement content)
        {
            return ContentOperations.GetParent(content)
                   ?? (content as FrameworkContentElement)?.Parent;
        }

        return VisualTreeHelper.GetParent(current);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e) => UpdateDropZone(sender, e);

    private void DropZone_DragOver(object sender, DragEventArgs e) => UpdateDropZone(sender, e);

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            ResetDropZone(border);
        }
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.IsWorkflowEditingEnabled
            || sender is not Border border
            || !TryGetPayload(e, out var payload)
            || !ViewModel.TryResolveDropTarget(border.Tag, out var destination, out var index))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (payload.ExistingActions is not null)
        {
            var copyRequested = payload.CopyRequested
                                || (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            if (copyRequested)
            {
                e.Effects = ViewModel.CopyActionsTo(payload.ExistingActions, destination, index)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            }
            else
            {
                e.Effects = ViewModel.MoveActions(payload.ExistingActions, destination, index)
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
            }
        }
        else if (payload.Kind is not null)
        {
            e.Effects = ViewModel.CreateAndInsert(payload.Kind.Value, destination, index) is not null
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        ResetDropZone(border);
        e.Handled = true;
    }

    private void UpdateDropZone(object sender, DragEventArgs e)
    {
        var copyRequested = false;
        if (!ViewModel.IsWorkflowEditingEnabled
            || sender is not Border border
            || !TryGetPayload(e, out var payload)
            || !ViewModel.TryResolveDropTarget(border.Tag, out var destination, out _)
            || (payload.ExistingActions is not null
                && !(copyRequested = payload.CopyRequested
                    || (e.KeyStates & DragDropKeyStates.ControlKey) != 0)
                && !ViewModel.CanMoveActions(payload.ExistingActions, destination)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = payload.ExistingActions is null || copyRequested
            ? DragDropEffects.Copy
            : DragDropEffects.Move;
        CancelInsertionFlash(border);
        if (border.MinHeight < 100)
        {
            border.Height = InsertionMarkerActiveHeight;
        }

        border.SetResourceReference(Border.BackgroundProperty, "DrillAccentSoftBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "DrillAccentBrush");
        border.BorderThickness = border.MinHeight >= 100
            ? new Thickness(2)
            : new Thickness(0, 2, 0, 0);
        e.Handled = true;
    }

    private void ResetDropZone(Border border)
    {
        if (_insertionFlashVersions.ContainsKey(border))
        {
            return;
        }

        border.Height = border.MinHeight >= 100 ? double.NaN : InsertionMarkerRestingHeight;
        border.Background = Brushes.Transparent;
        if (border.MinHeight >= 100)
        {
            border.SetResourceReference(Border.BorderBrushProperty, "DrillBorderBrush");
        }
        else
        {
            border.BorderBrush = Brushes.Transparent;
        }
        border.BorderThickness = border.MinHeight >= 100
            ? new Thickness(1)
            : new Thickness(0, 2, 0, 0);
    }

    private void EmptyDropSurface_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            ResetDropZone(border);
        }
    }

    private void InsertionMarker_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border && !_insertionFlashVersions.ContainsKey(border))
        {
            border.SetResourceReference(Border.BorderBrushProperty, "DrillAccentBrush");
            border.BorderThickness = new Thickness(0, 2, 0, 0);
        }
    }

    private void InsertionMarker_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border && !_insertionFlashVersions.ContainsKey(border))
        {
            ResetDropZone(border);
        }
    }

    private void InsertionMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || !ViewModel.SetPasteTarget(border.Tag))
        {
            return;
        }

        FlashInsertionMarker(border);
        e.Handled = true;
    }

    private void FlashInsertionMarker(Border border)
    {
        CancelInsertionFlash(border);
        var version = ++_nextInsertionFlashVersion;
        _insertionFlashVersions[border] = version;
        ShowInsertionHighlight(border);

        var flash = new DoubleAnimation
        {
            From = 1.0,
            To = 0.25,
            Duration = TimeSpan.FromMilliseconds(140),
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop
        };
        flash.Completed += (_, _) =>
        {
            if (!_insertionFlashVersions.TryGetValue(border, out var currentVersion)
                || currentVersion != version)
            {
                return;
            }

            _insertionFlashVersions.Remove(border);
            border.BeginAnimation(OpacityProperty, null);
            border.Opacity = 1.0;
            ResetDropZone(border);
        };
        border.BeginAnimation(OpacityProperty, flash, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelInsertionFlash(Border border)
    {
        _insertionFlashVersions.Remove(border);
        border.BeginAnimation(OpacityProperty, null);
        border.Opacity = 1.0;
    }

    private void ShowInsertionHighlight(Border border)
    {
        border.Height = border.MinHeight >= 100 ? double.NaN : InsertionMarkerRestingHeight;
        if (border.MinHeight >= 100)
        {
            border.SetResourceReference(Border.BackgroundProperty, "DrillAccentSoftBrush");
        }
        else
        {
            border.Background = Brushes.Transparent;
        }

        border.SetResourceReference(Border.BorderBrushProperty, "DrillAccentBrush");
        border.BorderThickness = border.MinHeight >= 100
            ? new Thickness(2)
            : new Thickness(0, 3, 0, 0);
    }

    private static bool TryGetPayload(DragEventArgs e, out WorkflowDragPayload payload)
    {
        payload = null!;
        if (!e.Data.GetDataPresent(DragFormat))
        {
            return false;
        }

        var value = e.Data.GetData(DragFormat) as WorkflowDragPayload;
        if (value is null)
        {
            return false;
        }

        payload = value;
        return true;
    }

    private void Parameter_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is DependencyObject dependencyObject
            && FindNearestAction(dependencyObject) is { } ownerAction)
        {
            ViewModel.EnsureActionSelected(ownerAction);
        }

        ViewModel.CaptureUndoCheckpoint();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isTextEditorFocused = Keyboard.FocusedElement is TextBoxBase;
        var isContinueShortcut = Keyboard.Modifiers == ModifierKeys.None
                                 && (e.Key == Key.F10
                                     || (e.Key == Key.System && e.SystemKey == Key.F10));
        var isResultImageZoomInShortcut =
            (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Add)
            || (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.OemPlus);
        var isResultImageZoomOutShortcut = Keyboard.Modifiers == ModifierKeys.None
                                           && (e.Key == Key.OemMinus || e.Key == Key.Subtract);
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S && ViewModel.SaveCommand.CanExecute(null))
        {
            ViewModel.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && ViewModel.UndoCommand.CanExecute(null))
        {
            ViewModel.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y && ViewModel.RedoCommand.CanExecute(null))
        {
            ViewModel.RedoCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && Keyboard.Modifiers == ModifierKeys.Control
                 && e.Key == Key.A)
        {
            ViewModel.SelectAllActions();
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && Keyboard.Modifiers == ModifierKeys.Control
                 && e.Key == Key.C
                 && ViewModel.CopySelectedCommand.CanExecute(null))
        {
            ViewModel.CopySelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && Keyboard.Modifiers == ModifierKeys.Control
                 && e.Key == Key.X
                 && ViewModel.CutSelectedCommand.CanExecute(null))
        {
            ViewModel.CutSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && Keyboard.Modifiers == ModifierKeys.Control
                 && e.Key == Key.V
                 && ViewModel.PasteCommand.CanExecute(null))
        {
            ViewModel.PasteCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && e.Key == Key.Delete
                 && ViewModel.DeleteSelectedCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused && e.Key == Key.Escape)
        {
            ViewModel.ClearActionSelection();
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && isResultImageZoomInShortcut
                 && ViewModel.ZoomSelectedResultImageInCommand.CanExecute(null))
        {
            ViewModel.ZoomSelectedResultImageInCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && isResultImageZoomOutShortcut
                 && ViewModel.ZoomSelectedResultImageOutCommand.CanExecute(null))
        {
            ViewModel.ZoomSelectedResultImageOutCommand.Execute(null);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                 && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            SetCanvasZoom(_canvasZoom + CanvasZoomStep);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                 && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            SetCanvasZoom(_canvasZoom - CanvasZoomStep);
            e.Handled = true;
        }
        else if (!isTextEditorFocused
                 && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                 && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            SetCanvasZoom(1.0);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && ViewModel.RunCommand.CanExecute(null))
        {
            ViewModel.RunCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F9 && ViewModel.ToggleBreakpointCommand.CanExecute(null))
        {
            ViewModel.ToggleBreakpointCommand.Execute(null);
            e.Handled = true;
        }
        else if (isContinueShortcut && ViewModel.ContinueCommand.CanExecute(null))
        {
            ViewModel.ContinueCommand.Execute(null);
            e.Handled = true;
        }
    }

    private sealed class WorkflowDragPayload
    {
        public WorkflowDragPayload(WorkflowNodeKind kind)
        {
            Kind = kind;
        }

        public WorkflowDragPayload(
            System.Collections.Generic.IReadOnlyList<WorkflowActionViewModel> actions,
            bool copyRequested)
        {
            ExistingActions = actions;
            CopyRequested = copyRequested;
        }

        public WorkflowNodeKind? Kind { get; }

        public System.Collections.Generic.IReadOnlyList<WorkflowActionViewModel>? ExistingActions { get; }

        public bool CopyRequested { get; }
    }
}
