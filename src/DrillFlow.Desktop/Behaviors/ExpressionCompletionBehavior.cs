using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DrillFlow.Core.Expressions;
using DrillFlow.Desktop.Services;

namespace DrillFlow.Desktop.Behaviors;

/// <summary>
/// Adds a caret-aware Ctrl+Space completion dropdown to a TextBox or the text
/// editor inside an editable ComboBox. The popup
/// deliberately contains a real ComboBox so it remains keyboard and mouse
/// accessible on .NET Framework 4.8/Windows 7 without a third-party editor.
/// </summary>
public static class ExpressionCompletionBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ExpressionCompletionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(IExpressionCompletionSource),
        typeof(ExpressionCompletionBehavior),
        new PropertyMetadata(null, OnContextChanged));

    public static readonly DependencyProperty OwnerNodeIdProperty = DependencyProperty.RegisterAttached(
        "OwnerNodeId",
        typeof(Guid),
        typeof(ExpressionCompletionBehavior),
        new PropertyMetadata(Guid.Empty, OnContextChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(EditorState),
        typeof(ExpressionCompletionBehavior),
        new PropertyMetadata(null));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetSource(DependencyObject element, IExpressionCompletionSource? value) =>
        element.SetValue(SourceProperty, value);

    public static IExpressionCompletionSource? GetSource(DependencyObject element) =>
        (IExpressionCompletionSource?)element.GetValue(SourceProperty);

    public static void SetOwnerNodeId(DependencyObject element, Guid value) =>
        element.SetValue(OwnerNodeIdProperty, value);

    public static Guid GetOwnerNodeId(DependencyObject element) =>
        (Guid)element.GetValue(OwnerNodeIdProperty);

    /// <summary>
    /// Converts the current editor value to an expression when needed and opens
    /// the same completion surface used by Ctrl+Space.
    /// </summary>
    public static bool BeginExpression(DependencyObject element)
    {
        if (element is ComboBox comboBox)
        {
            AttachEditableComboBox(comboBox);
        }

        if (!GetIsEnabled(element)
            || element.GetValue(StateProperty) is not EditorState state)
        {
            return false;
        }

        state.BeginExpression();
        return true;
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ComboBox comboBox)
        {
            comboBox.Loaded -= OnEditableComboBoxLoaded;
            comboBox.Unloaded -= OnEditableComboBoxUnloaded;

            if ((bool)e.NewValue)
            {
                comboBox.Loaded += OnEditableComboBoxLoaded;
                comboBox.Unloaded += OnEditableComboBoxUnloaded;
                if (comboBox.IsLoaded)
                {
                    AttachEditableComboBox(comboBox);
                }
            }
            else
            {
                (comboBox.GetValue(StateProperty) as EditorState)?.Close();
            }

            return;
        }

        if (dependencyObject is not TextBox textBox)
        {
            return;
        }

        var state = (EditorState?)textBox.GetValue(StateProperty);
        if ((bool)e.NewValue)
        {
            if (state == null)
            {
                state = new EditorState(textBox, textBox);
                textBox.SetValue(StateProperty, state);
            }
        }
        else
        {
            state?.Close();
        }
    }

    private static void OnContextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject.GetValue(StateProperty) is EditorState state)
        {
            state.RefreshIfOpen();
        }
    }

    private static void OnEditableComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox && GetIsEnabled(comboBox))
        {
            AttachEditableComboBox(comboBox);
        }
    }

    private static void OnEditableComboBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            (comboBox.GetValue(StateProperty) as EditorState)?.Close();
        }
    }

    private static void AttachEditableComboBox(ComboBox comboBox)
    {
        if (!comboBox.IsEditable || comboBox.GetValue(StateProperty) is EditorState)
        {
            return;
        }

        comboBox.ApplyTemplate();
        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox editor)
        {
            comboBox.SetValue(StateProperty, new EditorState(editor, comboBox));
        }
    }

    private sealed class EditorState
    {
        private readonly TextBox _editor;
        private readonly DependencyObject _contextOwner;
        private readonly Popup _popup;
        private readonly ComboBox _comboBox;
        private ExpressionCompletionResult _result = ExpressionCompletionResult.Empty(0);
        private bool _refreshing;

        public EditorState(TextBox editor, DependencyObject contextOwner)
        {
            _editor = editor;
            _contextOwner = contextOwner;
            _comboBox = new ComboBox
            {
                MinWidth = Math.Max(240, editor.ActualWidth),
                MaxWidth = 520,
                MaxDropDownHeight = 300,
                IsEditable = false,
                IsTextSearchEnabled = false,
                DisplayMemberPath = nameof(ExpressionCompletionItem.DisplayText)
            };

            var border = new Border
            {
                Padding = new Thickness(2),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 10,
                    Opacity = 0.22,
                    ShadowDepth = 2
                },
                Child = _comboBox
            };
            border.SetResourceReference(
                Border.BackgroundProperty,
                "DrillSurfaceBrush");
            border.SetResourceReference(
                Border.BorderBrushProperty,
                "DrillBorderBrush");

            _popup = new Popup
            {
                PlacementTarget = editor,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true,
                StaysOpen = true,
                Child = border
            };

            _editor.PreviewKeyDown += OnEditorPreviewKeyDown;
            _editor.TextChanged += OnEditorTextChanged;
            _editor.LostKeyboardFocus += OnEditorLostKeyboardFocus;
            _editor.Unloaded += OnEditorUnloaded;
            _comboBox.PreviewKeyDown += OnComboBoxPreviewKeyDown;
            _comboBox.PreviewMouseLeftButtonUp += OnComboBoxMouseLeftButtonUp;
        }

        public void RefreshIfOpen()
        {
            if (_popup.IsOpen)
            {
                Refresh(openWhenAvailable: false);
            }
        }

        public void BeginExpression()
        {
            var text = _editor.Text ?? string.Empty;
            var firstNonWhitespace = 0;
            while (firstNonWhitespace < text.Length
                   && char.IsWhiteSpace(text[firstNonWhitespace]))
            {
                firstNonWhitespace++;
            }

            if (firstNonWhitespace >= text.Length || text[firstNonWhitespace] != '=')
            {
                text = text.Insert(firstNonWhitespace, "=");
                SetEditorText(text);
            }

            FocusEditorAt(text.Length);
            Refresh(openWhenAvailable: true);
        }

        public void Close()
        {
            _comboBox.IsDropDownOpen = false;
            _popup.IsOpen = false;
            _result = ExpressionCompletionResult.Empty(_editor.CaretIndex);
        }

        private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space
                && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Refresh(openWhenAvailable: true);
                e.Handled = true;
                return;
            }

            if (!_popup.IsOpen)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                case Key.Tab:
                    CommitSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
            }
        }

        private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_popup.IsOpen)
            {
                return;
            }

            // TextChanged occurs before WPF has finalized CaretIndex for a keystroke.
            _editor.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => Refresh(openWhenAvailable: false)));
        }

        private void OnEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _editor.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!_editor.IsKeyboardFocusWithin && !_comboBox.IsKeyboardFocusWithin)
                    {
                        Close();
                    }
                }));
        }

        private void OnEditorUnloaded(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnComboBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                case Key.Tab:
                    CommitSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    _editor.Focus();
                    e.Handled = true;
                    break;
            }
        }

        private void OnComboBoxMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var current = e.OriginalSource as DependencyObject;
            while (current != null && current is not ComboBoxItem)
            {
                current = VisualTreeHelper.GetParent(current);
            }

            if (current is ComboBoxItem)
            {
                _editor.Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(CommitSelected));
            }
        }

        private void Refresh(bool openWhenAvailable)
        {
            var source = GetSource(_contextOwner);
            var ownerNodeId = GetOwnerNodeId(_contextOwner);
            if (!GetIsEnabled(_contextOwner)
                || source == null
                || !source.CanCompleteExpressions
                || ownerNodeId == Guid.Empty)
            {
                Close();
                return;
            }

            var result = source.GetExpressionCompletions(
                ownerNodeId,
                _editor.Text ?? string.Empty,
                _editor.CaretIndex);
            if (result.Items.Count == 0)
            {
                Close();
                return;
            }

            _result = result;
            _refreshing = true;
            try
            {
                _comboBox.MinWidth = Math.Max(240, _editor.ActualWidth);
                _comboBox.ItemsSource = result.Items;
                _comboBox.SelectedIndex = 0;
            }
            finally
            {
                _refreshing = false;
            }

            if (openWhenAvailable || _popup.IsOpen)
            {
                _popup.IsOpen = true;
                _editor.Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() =>
                    {
                        if (_popup.IsOpen)
                        {
                            _comboBox.IsDropDownOpen = true;
                            _editor.Focus();
                        }
                    }));
            }
        }

        private void MoveSelection(int delta)
        {
            if (_comboBox.Items.Count == 0)
            {
                return;
            }

            var current = _comboBox.SelectedIndex < 0 ? 0 : _comboBox.SelectedIndex;
            var next = (current + delta + _comboBox.Items.Count) % _comboBox.Items.Count;
            _refreshing = true;
            try
            {
                _comboBox.SelectedIndex = next;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void CommitSelected()
        {
            if (_refreshing
                || _comboBox.SelectedItem is not ExpressionCompletionItem item
                || !GetIsEnabled(_contextOwner)
                || GetSource(_contextOwner)?.CanCompleteExpressions != true)
            {
                return;
            }

            var current = _editor.Text ?? string.Empty;
            var start = Math.Max(0, Math.Min(_result.ReplacementStart, current.Length));
            var length = Math.Max(0, Math.Min(_result.ReplacementLength, current.Length - start));
            var updated = current.Remove(start, length).Insert(start, item.InsertionText);
            var caretIndex = start + item.InsertionText.Length;

            Close();
            SetEditorText(updated);
            FocusEditorAt(caretIndex);
        }

        private void SetEditorText(string text)
        {
            _editor.Text = text;
            if (_contextOwner is ComboBox editableComboBox)
            {
                editableComboBox.Text = text;
                editableComboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            }
            else
            {
                _editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            _editor.CaretIndex = Math.Min(text.Length, _editor.Text.Length);
        }

        private void FocusEditorAt(int caretIndex)
        {
            void CollapseSelection()
            {
                var boundedCaret = Math.Max(0, Math.Min(caretIndex, _editor.Text?.Length ?? 0));
                _editor.CaretIndex = boundedCaret;
                _editor.Select(boundedCaret, 0);
            }

            _editor.Focus();
            CollapseSelection();

            // Editable ComboBox applies its Text binding and focus selection after
            // the completion click has returned. Collapse it again on the next
            // dispatcher pass so the next keystroke appends instead of replacing
            // the whole expression.
            _editor.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (_editor.IsLoaded)
                    {
                        _editor.Focus();
                        CollapseSelection();
                    }
                }));
        }
    }
}
