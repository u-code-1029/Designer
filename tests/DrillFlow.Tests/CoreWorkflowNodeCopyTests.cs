using System;
using System.Linq;
using DrillFlow.Core.Workflows;
using Xunit;

namespace DrillFlow.Tests
{
    public sealed class CoreWorkflowNodeCopyTests
    {
        [Fact]
        public void CloneForInsertionDeepCopiesNestedNodesWithFreshIdentitiesAndUniqueAliases()
        {
            var inner = new FocusNode { Key = "focus_1" };
            var repeat = new RepeatNode { Key = "repeat_1" };
            repeat.Body.Add(inner);

            var clone = Assert.IsType<RepeatNode>(WorkflowNodeCopy.CloneForInsertion(
                repeat,
                new[] { "repeat_1", "repeat_1_copy", "focus_1" }));

            var clonedInner = Assert.IsType<FocusNode>(Assert.Single(clone.Body));
            Assert.Equal("repeat_1_copy2", clone.Key);
            Assert.Equal("focus_1_copy", clonedInner.Key);
            Assert.NotEqual(repeat.Id, clone.Id);
            Assert.NotEqual(inner.Id, clonedInner.Id);
            Assert.NotSame(repeat.Count, clone.Count);
            Assert.NotSame(inner.HorizontalFieldWidth, clonedInner.HorizontalFieldWidth);
        }

        [Fact]
        public void CloneForInsertionRewritesOnlyReferencesToActionsInsideCopiedSubtree()
        {
            var first = new FocusNode { Key = "focus" };
            var stage = new StageNode
            {
                Key = "stage_1",
                StageX = ParameterBinding.Expression(
                    "focus.result.value + external.result.offset + 'focus.result.literal'")
            };
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Condition = ParameterBinding.Expression(
                "stage_1.parameters.stage_x > 0 && stage_1 != null && external.result.ready");
            conditional.Branches[0].Body.Add(first);
            conditional.Branches[0].Body.Add(stage);

            var clone = Assert.IsType<ConditionalNode>(WorkflowNodeCopy.CloneForInsertion(
                conditional,
                new[] { "choice", "focus", "stage_1" }));

            var clonedNodes = clone.Branches[0].Body.ToArray();
            var clonedStage = Assert.IsType<StageNode>(clonedNodes[1]);
            Assert.Equal(
                "=focus_copy.result.value + external.result.offset + 'focus.result.literal'",
                clonedStage.StageX.RawText);
            Assert.Equal(
                "=stage_1_copy.parameters.stage_x > 0 && stage_1_copy != null && external.result.ready",
                clone.Branches[0].Condition!.RawText);
            Assert.NotEqual(conditional.Branches[0].Id, clone.Branches[0].Id);
        }

        [Fact]
        public void CloneManyForInsertionPreservesOrderAndRewritesReferencesAcrossSelectedRoots()
        {
            var focus = new FocusNode { Key = "focus_1" };
            var integration = new IntegrationNode
            {
                Key = "integration_1",
                HorizontalFieldWidth = ParameterBinding.Expression(
                    "focus_1.parameters.hfw + external.parameters.offset")
            };

            var clones = WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { focus, integration },
                new[] { "focus_1", "integration_1" });

            var clonedFocus = Assert.IsType<FocusNode>(clones[0]);
            var clonedIntegration = Assert.IsType<IntegrationNode>(clones[1]);
            Assert.Equal("focus_1_copy", clonedFocus.Key);
            Assert.Equal("integration_1_copy", clonedIntegration.Key);
            Assert.Equal(
                "=focus_1_copy.parameters.hfw + external.parameters.offset",
                clonedIntegration.HorizontalFieldWidth.RawText);
            Assert.NotEqual(focus.Id, clonedFocus.Id);
            Assert.NotEqual(integration.Id, clonedIntegration.Id);
        }

        [Fact]
        public void CloneManyForInsertionDeepCopiesEveryEquipmentActionBinding()
        {
            WorkflowNode[] sources =
            {
                new StageNode { Key = "stage_1" },
                new CameraNode { Key = "camera_1" },
                new FocusNode { Key = "focus_1" },
                new IntegrationNode { Key = "integration_1" },
                new LiveNode { Key = "live_1" },
                new AbortNode { Key = "abort_1" }
            };

            var clones = WorkflowNodeCopy.CloneManyForInsertion(
                sources,
                sources.Select(node => node.Key));

            Assert.Collection(
                clones,
                node => Assert.IsType<StageNode>(node),
                node => Assert.IsType<CameraNode>(node),
                node => Assert.IsType<FocusNode>(node),
                node => Assert.IsType<IntegrationNode>(node),
                node => Assert.IsType<LiveNode>(node),
                node => Assert.IsType<AbortNode>(node));
            for (var index = 0; index < sources.Length; index++)
            {
                Assert.NotEqual(sources[index].Id, clones[index].Id);
                Assert.Equal(sources[index].Key + "_copy", clones[index].Key);
                var sourceBindings = sources[index].GetParameterBindings();
                var cloneBindings = clones[index].GetParameterBindings();
                Assert.Equal(sourceBindings.Keys, cloneBindings.Keys);
                foreach (var key in sourceBindings.Keys)
                {
                    Assert.NotSame(sourceBindings[key], cloneBindings[key]);
                    Assert.Equal(sourceBindings[key].RawText, cloneBindings[key].RawText);
                }
            }
        }

        [Fact]
        public void CloneManyForInsertionAcceptsEmptyBatchAndNullExistingAliases()
        {
            Assert.Empty(WorkflowNodeCopy.CloneManyForInsertion(
                Array.Empty<WorkflowNode>(),
                existingAliases: null!));

            var source = new DelayNode { Key = "delay_1" };
            var clone = Assert.IsType<DelayNode>(Assert.Single(
                WorkflowNodeCopy.CloneManyForInsertion(new[] { source }, existingAliases: null!)));
            Assert.Equal("delay_1_copy", clone.Key);
        }

        [Fact]
        public void CloneManyForInsertionRejectsNullDuplicateAndOverlappingSources()
        {
            Assert.Throws<ArgumentNullException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                null!,
                Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { new StageNode(), null! },
                Array.Empty<string>()));

            var repeatedRoot = new FocusNode { Key = "focus_1" };
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { repeatedRoot, repeatedRoot },
                Array.Empty<string>()));

            var child = new DelayNode { Key = "delay_child" };
            var parent = new RepeatNode { Key = "repeat_parent" };
            parent.Body.Add(child);
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { parent, child },
                Array.Empty<string>()));

            var duplicateAlias = new AbortNode { Key = "FOCUS_1" };
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { repeatedRoot, duplicateAlias },
                Array.Empty<string>()));

            var duplicateIdentity = new AbortNode
            {
                Id = repeatedRoot.Id,
                Key = "different_alias"
            };
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { repeatedRoot, duplicateIdentity },
                Array.Empty<string>()));
        }

        [Fact]
        public void CloneManyForInsertionUsesCaseInsensitiveAliasesAcrossTheWholeBatch()
        {
            var first = new FocusNode { Key = "Action" };
            var second = new DelayNode
            {
                Key = "Wait",
                DurationMilliseconds = ParameterBinding.Expression("ACTION.result.timeout_ms")
            };

            var clones = WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { first, second },
                new[] { "action_copy", "ACTION_COPY2", "wait_copy", "WAIT_COPY" });

            Assert.Equal("Action_copy3", clones[0].Key);
            var delay = Assert.IsType<DelayNode>(clones[1]);
            Assert.Equal("Wait_copy2", delay.Key);
            Assert.Equal("=Action_copy3.result.timeout_ms", delay.DurationMilliseconds.RawText);
        }

        [Fact]
        public void CloneManyForInsertionDeepCopiesHttpExpressionsInsideNestedContainers()
        {
            var seed = new FocusNode { Key = "seed" };
            var http = new HttpActionNode
            {
                Key = "http_call",
                Method = ParameterBinding.Expression("seed.result.method"),
                Url = ParameterBinding.Expression("seed.result.url"),
                Headers = ParameterBinding.Expression("seed.result.headers"),
                Body = ParameterBinding.Expression("seed.result.payload"),
                TimeoutMilliseconds = ParameterBinding.Expression("seed.result.timeout_ms")
            };
            var repeat = new RepeatNode
            {
                Key = "group",
                Count = ParameterBinding.Expression("seed.result.count")
            };
            repeat.Body.Add(http);

            var clones = WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { seed, repeat },
                new[] { "seed", "http_call", "group" });

            var clonedSeed = Assert.IsType<FocusNode>(clones[0]);
            var clonedRepeat = Assert.IsType<RepeatNode>(clones[1]);
            var clonedHttp = Assert.IsType<HttpActionNode>(Assert.Single(clonedRepeat.Body));
            Assert.Equal("seed_copy", clonedSeed.Key);
            Assert.Equal("group_copy", clonedRepeat.Key);
            Assert.Equal("http_call_copy", clonedHttp.Key);
            Assert.Equal("=seed_copy.result.count", clonedRepeat.Count.RawText);
            Assert.Equal("=seed_copy.result.method", clonedHttp.Method.RawText);
            Assert.Equal("=seed_copy.result.url", clonedHttp.Url.RawText);
            Assert.Equal("=seed_copy.result.headers", clonedHttp.Headers.RawText);
            Assert.Equal("=seed_copy.result.payload", clonedHttp.Body.RawText);
            Assert.Equal("=seed_copy.result.timeout_ms", clonedHttp.TimeoutMilliseconds.RawText);
            Assert.NotSame(http.Method, clonedHttp.Method);
            Assert.NotSame(http.Url, clonedHttp.Url);
            Assert.NotSame(http.Headers, clonedHttp.Headers);
            Assert.NotSame(http.Body, clonedHttp.Body);
            Assert.NotSame(http.TimeoutMilliseconds, clonedHttp.TimeoutMilliseconds);
            Assert.NotEqual(http.Id, clonedHttp.Id);
        }
    }
}
