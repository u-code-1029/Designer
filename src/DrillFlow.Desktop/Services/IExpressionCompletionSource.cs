using System;
using DrillFlow.Core.Expressions;

namespace DrillFlow.Desktop.Services;

/// <summary>
/// Bridges the reusable WPF text-box behavior to the current workflow without
/// coupling the behavior to MainPage or a concrete view model.
/// </summary>
public interface IExpressionCompletionSource
{
    bool CanCompleteExpressions { get; }

    ExpressionCompletionResult GetExpressionCompletions(
        Guid ownerNodeId,
        string rawText,
        int caretIndex);
}
