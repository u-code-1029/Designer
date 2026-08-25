using System;
using System.Collections.Generic;
using System.Linq;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Workflows;
using Xunit;

namespace DrillFlow.Tests;

public sealed class CoreExpressionCompletionProviderTests
{
    private readonly ExpressionCompletionProvider _provider = new();

    [Fact]
    public void RootCompletion_OffersOnlyEnabledEarlierGuaranteedActions()
    {
        var first = Move("first");
        var disabled = Move("disabled");
        disabled.IsEnabled = false;
        var current = new DrillNode { Key = "current" };
        var later = Move("later");
        var document = Document(first, disabled, current, later);

        var result = Complete(document, current, "=");

        Assert.Equal(new[] { "first" }, result.Items.Select(item => item.DisplayText));
        Assert.Equal(1, result.ReplacementStart);
        Assert.Equal(0, result.ReplacementLength);
    }

    [Fact]
    public void Completion_IsUnavailableForLiteralValues()
    {
        var first = Move("first");
        var current = new DrillNode { Key = "current" };

        var result = Complete(Document(first, current), current, "1E-3");

        Assert.Empty(result.Items);
    }

    [Fact]
    public void Completion_IsUnavailableInsideStringLiteral()
    {
        var first = Move("first");
        var current = new DrillNode { Key = "current" };
        const string raw = "='first.result.value'";
        var caret = raw.IndexOf("result", StringComparison.Ordinal) + 3;

        var result = _provider.GetCompletions(Document(first, current), current.Id, raw, caret);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void ActionMemberCompletion_FiltersAndReplacesOnlyCurrentFragment()
    {
        var first = Move("move_1");
        var current = new DrillNode { Key = "current" };
        var raw = "=move_1.re";

        var result = Complete(Document(first, current), current, raw);

        Assert.Equal(new[] { "result", "results" }, result.Items.Select(item => item.DisplayText));
        Assert.Equal(raw.IndexOf("re", StringComparison.Ordinal), result.ReplacementStart);
        Assert.Equal(2, result.ReplacementLength);
        Assert.Equal("result", result.Items[0].InsertionText);
    }

    [Fact]
    public void CompletionAtMiddleOfToken_ReplacesTheWholeToken()
    {
        var first = Move("move_1");
        var current = new DrillNode { Key = "current" };
        var raw = "=move_1.remainder + 1";
        var caret = raw.IndexOf("remainder", StringComparison.Ordinal) + 2;

        var result = _provider.GetCompletions(
            Document(first, current),
            current.Id,
            raw,
            caret);

        Assert.Equal(raw.IndexOf("remainder", StringComparison.Ordinal), result.ReplacementStart);
        Assert.Equal("remainder".Length, result.ReplacementLength);
        Assert.Equal(new[] { "result", "results" }, result.Items.Select(item => item.DisplayText));
    }

    [Fact]
    public void ExactObjectCompletion_AppendsParameterMemberWithoutReplacingObject()
    {
        var first = Move("move_1");
        var current = new DrillNode { Key = "current" };
        var raw = "=move_1.parameters";

        var result = Complete(Document(first, current), current, raw);

        Assert.Equal(raw.Length, result.ReplacementStart);
        Assert.Equal(0, result.ReplacementLength);
        Assert.Contains(result.Items, item =>
            item.DisplayText == "move_x" && item.InsertionText == ".move_x");
    }

    [Fact]
    public void ResultCompletion_IncludesContractAndObservedDynamicFields()
    {
        var first = Move("move_1");
        var current = new DrillNode { Key = "current" };
        var observed = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [first.Id] = new[] { "laser_distance", "command" }
        };

        var result = _provider.GetCompletions(
            Document(first, current),
            current.Id,
            "=move_1.result.",
            "=move_1.result.".Length,
            observed);

        Assert.Contains(result.Items, item => item.DisplayText == "index");
        Assert.Contains(result.Items, item => item.DisplayText == "command");
        Assert.Contains(result.Items, item => item.DisplayText == "position_x");
        Assert.Contains(result.Items, item => item.DisplayText == "position_y");
        Assert.Contains(result.Items, item => item.DisplayText == "laser_distance");
        Assert.Single(result.Items, item => item.DisplayText == "command");
    }

    [Fact]
    public void ResultCompletion_UsesCommandSpecificTestResponseFields()
    {
        var move = Move("move_1");
        var measure = new MeasureNode { Key = "measure_1" };
        var drill = new DrillNode { Key = "drill_1" };
        var current = new DelayNode { Key = "current" };
        var document = Document(move, measure, drill, current);

        var measureItems = Complete(document, current, "=measure_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();
        var drillItems = Complete(document, current, "=drill_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();

        Assert.Contains("measured_distance", measureItems);
        Assert.Contains("drill_result_path", drillItems);
    }

    [Fact]
    public void RepeatBody_SeesEarlierBodyAction_AndExportsItAfterEnabledRepeat()
    {
        var outer = Move("outer");
        var bodyFirst = Move("body_first");
        var bodyCurrent = new DrillNode { Key = "body_current" };
        var repeat = new RepeatNode { Key = "repeat_1", Body = { bodyFirst, bodyCurrent } };
        var after = new DrillNode { Key = "after" };
        var document = Document(outer, repeat, after);

        var insideItems = Complete(document, bodyCurrent, "=").Items.Select(item => item.DisplayText).ToArray();
        var afterItems = Complete(document, after, "=").Items.Select(item => item.DisplayText).ToArray();

        Assert.Equal(new[] { "outer", "body_first" }, insideItems);
        Assert.Contains("body_first", afterItems);
        Assert.Contains("body_current", afterItems);
        Assert.Contains("repeat_1", afterItems);
    }

    [Fact]
    public void ConditionalBranchAliases_DoNotEscapeConditional()
    {
        var outer = Move("outer");
        var branchFirst = Move("branch_first");
        var branchCurrent = new DrillNode { Key = "branch_current" };
        var conditional = new ConditionalNode { Key = "if_1" };
        conditional.Branches[0].Body.Add(branchFirst);
        conditional.Branches[0].Body.Add(branchCurrent);
        var after = new DrillNode { Key = "after" };
        var document = Document(outer, conditional, after);

        var inside = Complete(document, branchCurrent, "=").Items.Select(item => item.DisplayText).ToArray();
        var outside = Complete(document, after, "=").Items.Select(item => item.DisplayText).ToArray();

        Assert.Equal(new[] { "outer", "branch_first" }, inside);
        Assert.Equal(new[] { "outer", "if_1" }, outside);
    }

    [Fact]
    public void ResultsArrayCompletion_SupportsLastCountAndIndexNavigation()
    {
        var first = Move("move_1");
        var current = new DrillNode { Key = "current" };
        var document = Document(first, current);

        var exact = Complete(document, current, "=move_1.results");
        var indexed = Complete(document, current, "=move_1.results[0].");

        Assert.Contains(exact.Items, item => item.InsertionText == ".last");
        Assert.Contains(exact.Items, item => item.InsertionText == "[0]");
        Assert.Contains(indexed.Items, item => item.DisplayText == "index");
    }

    private ExpressionCompletionResult Complete(
        WorkflowDocument document,
        WorkflowNode owner,
        string raw)
    {
        return _provider.GetCompletions(document, owner.Id, raw, raw.Length);
    }

    private static WorkflowDocument Document(params WorkflowNode[] nodes)
    {
        var document = new WorkflowDocument { Name = "Completion tests" };
        document.Nodes.AddRange(nodes);
        return document;
    }

    private static MoveNode Move(string key)
    {
        return new MoveNode { Key = key };
    }
}
