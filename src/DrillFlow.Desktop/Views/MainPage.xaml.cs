using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Views;

public partial class MainPage : Page
{
    private const string DragFormat = "DrillFlow.WorkflowDragPayload";
    private Point _dragStart;

    public MainPage(MainPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainPageViewModel ViewModel => (MainPageViewModel)DataContext;

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
            ViewModel.SelectAction(action);
            element.Focus();
            e.Handled = true;
        }
    }

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsWorkflowEditingEnabled
            && sender is FrameworkElement element
            && element.DataContext is WorkflowActionViewModel action)
        {
            _dragStart = e.GetPosition(this);
            ViewModel.SelectAction(action);
            e.Handled = true;
        }
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!ViewModel.IsWorkflowEditingEnabled
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element
            || element.DataContext is not WorkflowActionViewModel action)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(DragFormat, new WorkflowDragPayload(action));
        DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
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

        if (payload.ExistingAction is not null)
        {
            e.Effects = ViewModel.MoveAction(payload.ExistingAction, destination, index)
                ? DragDropEffects.Move
                : DragDropEffects.None;
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
        if (!ViewModel.IsWorkflowEditingEnabled
            || sender is not Border border
            || !TryGetPayload(e, out var payload)
            || !ViewModel.TryResolveDropTarget(border.Tag, out var destination, out _)
            || (payload.ExistingAction is not null && !ViewModel.CanMoveAction(payload.ExistingAction, destination)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = payload.ExistingAction is null ? DragDropEffects.Copy : DragDropEffects.Move;
        border.Height = 22;
        border.Background = (Brush)FindResource("DrillAccentSoftBrush");
        border.BorderBrush = (Brush)FindResource("DrillAccentBrush");
        border.BorderThickness = new Thickness(1);
        e.Handled = true;
    }

    private static void ResetDropZone(Border border)
    {
        border.Height = 12;
        border.Background = Brushes.Transparent;
        border.BorderThickness = new Thickness(0);
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
        ViewModel.CaptureUndoCheckpoint();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        else if (e.Key == Key.Delete && ViewModel.DeleteSelectedCommand.CanExecute(null))
        {
            ViewModel.DeleteSelectedCommand.Execute(null);
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
        else if (e.Key == Key.F10 && ViewModel.StepCommand.CanExecute(null))
        {
            ViewModel.StepCommand.Execute(null);
            e.Handled = true;
        }
    }

    private sealed class WorkflowDragPayload
    {
        public WorkflowDragPayload(WorkflowNodeKind kind)
        {
            Kind = kind;
        }

        public WorkflowDragPayload(WorkflowActionViewModel action)
        {
            ExistingAction = action;
        }

        public WorkflowNodeKind? Kind { get; }

        public WorkflowActionViewModel? ExistingAction { get; }
    }
}
