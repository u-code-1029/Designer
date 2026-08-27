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
        var first = Stage("first");
        var disabled = Stage("disabled");
        disabled.IsEnabled = false;
        var current = new IntegrationNode { Key = "current" };
        var later = Stage("later");
        var document = Document(first, disabled, current, later);

        var result = Complete(document, current, "=");

        Assert.Equal(new[] { "first" }, result.Items.Select(item => item.DisplayText));
        Assert.Equal(1, result.ReplacementStart);
        Assert.Equal(0, result.ReplacementLength);
    }

    [Fact]
    public void Completion_IsUnavailableForLiteralValues()
    {
        var first = Stage("first");
        var current = new IntegrationNode { Key = "current" };

        var result = Complete(Document(first, current), current, "1E-3");

        Assert.Empty(result.Items);
    }

    [Fact]
    public void Completion_IsUnavailableInsideStringLiteral()
    {
        var first = Stage("first");
        var current = new IntegrationNode { Key = "current" };
        const string raw = "='first.result.value'";
        var caret = raw.IndexOf("result", StringComparison.Ordinal) + 3;

        var result = _provider.GetCompletions(Document(first, current), current.Id, raw, caret);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void ActionMemberCompletion_FiltersAndReplacesOnlyCurrentFragment()
    {
        var first = Stage("stage_1");
        var current = new IntegrationNode { Key = "current" };
        var raw = "=stage_1.re";

        var result = Complete(Document(first, current), current, raw);

        Assert.Equal(new[] { "result", "results" }, result.Items.Select(item => item.DisplayText));
        Assert.Equal(raw.IndexOf("re", StringComparison.Ordinal), result.ReplacementStart);
        Assert.Equal(2, result.ReplacementLength);
        Assert.Equal("result", result.Items[0].InsertionText);
    }

    [Fact]
    public void CompletionAtMiddleOfToken_ReplacesTheWholeToken()
    {
        var first = Stage("stage_1");
        var current = new IntegrationNode { Key = "current" };
        var raw = "=stage_1.remainder + 1";
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
        var first = Stage("stage_1");
        var current = new IntegrationNode { Key = "current" };
        var raw = "=stage_1.parameters";

        var result = Complete(Document(first, current), current, raw);

        Assert.Equal(raw.Length, result.ReplacementStart);
        Assert.Equal(0, result.ReplacementLength);
        Assert.Contains(result.Items, item =>
            item.DisplayText == "stage_x" && item.InsertionText == ".stage_x");
    }

    [Fact]
    public void ResultCompletion_IncludesContractAndObservedDynamicFields()
    {
        var first = Stage("stage_1");
        var current = new IntegrationNode { Key = "current" };
        var observed = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [first.Id] = new[] { "controller_value", "action" }
        };

        var result = _provider.GetCompletions(
            Document(first, current),
            current.Id,
            "=stage_1.result.",
            "=stage_1.result.".Length,
            observed);

        Assert.Contains(result.Items, item => item.DisplayText == "type");
        Assert.Contains(result.Items, item => item.DisplayText == "correlation_id");
        Assert.Contains(result.Items, item => item.DisplayText == "action");
        Assert.Contains(result.Items, item => item.DisplayText == "result");
        Assert.Contains(result.Items, item => item.DisplayText == "current_stage_x");
        Assert.Contains(result.Items, item => item.DisplayText == "current_stage_y");
        Assert.Contains(result.Items, item => item.DisplayText == "controller_value");
        Assert.DoesNotContain(result.Items, item => item.DisplayText == "index");
        Assert.Single(result.Items, item => item.DisplayText == "action");
    }

    [Fact]
    public void ResultCompletion_UsesActionSpecificEquipmentResponseFields()
    {
        var stage = Stage("stage_1");
        var camera = new CameraNode { Key = "camera_1" };
        var focus = new FocusNode { Key = "focus_1" };
        var integration = new IntegrationNode { Key = "integration_1" };
        var live = new LiveNode { Key = "live_1" };
        var abort = new AbortNode { Key = "abort_1" };
        var current = new DelayNode { Key = "current" };
        var document = Document(stage, camera, focus, integration, live, abort, current);

        var stageItems = Complete(document, current, "=stage_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();
        var cameraItems = Complete(document, current, "=camera_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();
        var focusItems = Complete(document, current, "=focus_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();
        var integrationItems = Complete(document, current, "=integration_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();
        var liveItems = Complete(document, current, "=live_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();
        var abortItems = Complete(document, current, "=abort_1.result.")
            .Items.Select(item => item.DisplayText).ToArray();

        Assert.Contains("current_stage_x", stageItems);
        Assert.Contains("current_stage_y", stageItems);
        Assert.Contains("current_camera_x", cameraItems);
        Assert.Contains("current_camera_y", cameraItems);
        Assert.Contains("z_to_sharpness_2d", focusItems);
        Assert.Contains("hfw", integrationItems);
        Assert.Contains("frame_count", integrationItems);
        Assert.Contains("image_path", integrationItems);
        Assert.Contains("hfw", liveItems);
        Assert.Contains("frame_count", liveItems);
        Assert.Contains("image_path", liveItems);
        Assert.Contains("result", abortItems);
        Assert.DoesNotContain("image_path", stageItems);
        Assert.DoesNotContain("current_stage_x", cameraItems);
    }

    [Fact]
    public void RepeatBody_SeesEarlierBodyAction_AndExportsItAfterEnabledRepeat()
    {
        var outer = Stage("outer");
        var bodyFirst = Stage("body_first");
        var bodyCurrent = new IntegrationNode { Key = "body_current" };
        var repeat = new RepeatNode { Key = "repeat_1", Body = { bodyFirst, bodyCurrent } };
        var after = new IntegrationNode { Key = "after" };
        var document = Document(outer, repeat, after);

        var insideItems = Complete(document, bodyCurrent, "=").Items.Select(item => item.DisplayText).ToArray();
        var afterItems = Complete(document, after, "=").Items.Select(item => item.DisplayText).ToArray();

        Assert.Equal(new[] { "outer", "body_first" }, insideItems);
        Assert.Contains("body_first", afterItems);
        Assert.Contains("body_current", afterItems);
        Assert.Contains("repeat_1", afterItems);
    }

    [Fact]
    public void ConditionalConditionCompletion_UsesConditionalAsOwner()
    {
        var earlier = Stage("earlier");
        var disabled = Stage("disabled");
        disabled.IsEnabled = false;
        var conditional = new ConditionalNode { Key = "if_1" };
        var later = Stage("later");
        var document = Document(earlier, disabled, conditional, later);

        var result = Complete(document, conditional, "=");

        Assert.Equal(new[] { "earlier" }, result.Items.Select(item => item.DisplayText));
    }

    [Fact]
    public void ConditionalBranchAliases_DoNotEscapeConditional()
    {
        var outer = Stage("outer");
        var branchFirst = Stage("branch_first");
        var branchCurrent = new IntegrationNode { Key = "branch_current" };
        var conditional = new ConditionalNode { Key = "if_1" };
        conditional.Branches[0].Body.Add(branchFirst);
        conditional.Branches[0].Body.Add(branchCurrent);
        var after = new IntegrationNode { Key = "after" };
        var document = Document(outer, conditional, after);

        var inside = Complete(document, branchCurrent, "=").Items.Select(item => item.DisplayText).ToArray();
        var outside = Complete(document, after, "=").Items.Select(item => item.DisplayText).ToArray();

        Assert.Equal(new[] { "outer", "branch_first" }, inside);
        Assert.Equal(new[] { "outer", "if_1" }, outside);
    }

    [Fact]
    public void ResultsArrayCompletion_SupportsLastCountAndIndexNavigation()
    {
        var first = Stage("stage_1");
        var current = new IntegrationNode { Key = "current" };
        var document = Document(first, current);

        var exact = Complete(document, current, "=stage_1.results");
        var indexed = Complete(document, current, "=stage_1.results[0].");

        Assert.Contains(exact.Items, item => item.InsertionText == ".last");
        Assert.Contains(exact.Items, item => item.InsertionText == "[0]");
        Assert.Contains(indexed.Items, item => item.DisplayText == "correlation_id");
        Assert.DoesNotContain(indexed.Items, item => item.DisplayText == "index");
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

    private static StageNode Stage(string key)
    {
        return new StageNode { Key = key };
    }
}
