using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DrillFlow.Core.Expressions;
using DrillFlow.Desktop.Behaviors;
using DrillFlow.Desktop.Services;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopExpressionCompletionBehaviorTests
{
    [Fact]
    public void BeginExpression_PreservesLiteralAndCollapsesSelectionAtEnd()
    {
        RunOnSta(() =>
        {
            var editor = new TextBox { Text = "  1E-3" };
            ConfigureContext(editor);

            var opened = ExpressionCompletionBehavior.BeginExpression(editor);

            Assert.True(opened);
            Assert.Equal("  =1E-3", editor.Text);
            Assert.Equal(editor.Text.Length, editor.CaretIndex);
            Assert.Equal(0, editor.SelectionLength);
        });
    }

    [Fact]
    public void CommitCompletion_OnEditableComboBox_PreservesExpressionAndCollapsesSelection()
    {
        RunOnSta(() =>
        {
            var owner = new ComboBox
            {
                IsEditable = true,
                Text = "=sta"
            };
            var editor = new TextBox { Text = "=sta" };
            ConfigureContext(owner);

            var behaviorType = typeof(ExpressionCompletionBehavior);
            var stateType = behaviorType.GetNestedType(
                "EditorState",
                BindingFlags.NonPublic) ?? throw new InvalidOperationException("EditorState was not found.");
            var state = Activator.CreateInstance(
                stateType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { editor, owner },
                culture: null) ?? throw new InvalidOperationException("EditorState could not be created.");

            var completion = new ExpressionCompletionItem(
                "stage_1",
                "stage_1",
                "Stage action");
            SetField(stateType, state, "_result", new ExpressionCompletionResult(
                new[] { completion },
                replacementStart: 1,
                replacementLength: 3));

            var completionBox = (ComboBox)GetField(stateType, state, "_comboBox");
            completionBox.ItemsSource = new[] { completion };
            completionBox.SelectedIndex = 0;

            stateType.GetMethod(
                    "CommitSelected",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(state, null);

            Assert.Equal("=stage_1", owner.Text);
            Assert.Equal("=stage_1", editor.Text);
            Assert.Equal(editor.Text.Length, editor.CaretIndex);
            Assert.Equal(0, editor.SelectionLength);
        });
    }

    private static void ConfigureContext(DependencyObject owner)
    {
        ExpressionCompletionBehavior.SetSource(owner, new CompletionSourceStub());
        ExpressionCompletionBehavior.SetOwnerNodeId(owner, Guid.NewGuid());
        ExpressionCompletionBehavior.SetIsEnabled(owner, true);
    }

    private static object GetField(Type type, object instance, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException(name + " was not found.");

    private static void SetField(Type type, object instance, string name, object value)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(name + " was not found.");
        field.SetValue(instance, value);
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured != null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private sealed class CompletionSourceStub : IExpressionCompletionSource
    {
        public bool CanCompleteExpressions => true;

        public ExpressionCompletionResult GetExpressionCompletions(
            Guid ownerNodeId,
            string rawText,
            int caretIndex) => ExpressionCompletionResult.Empty(caretIndex);
    }
}
