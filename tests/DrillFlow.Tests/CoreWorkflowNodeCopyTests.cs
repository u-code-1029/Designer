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
            var inner = new MeasureNode { Key = "measure_1" };
            var repeat = new RepeatNode { Key = "repeat_1" };
            repeat.Body.Add(inner);

            var clone = Assert.IsType<RepeatNode>(WorkflowNodeCopy.CloneForInsertion(
                repeat,
                new[] { "repeat_1", "repeat_1_copy", "measure_1" }));

            var clonedInner = Assert.IsType<MeasureNode>(Assert.Single(clone.Body));
            Assert.Equal("repeat_1_copy2", clone.Key);
            Assert.Equal("measure_1_copy", clonedInner.Key);
            Assert.NotEqual(repeat.Id, clone.Id);
            Assert.NotEqual(inner.Id, clonedInner.Id);
            Assert.NotSame(repeat.Count, clone.Count);
            Assert.NotSame(inner.Thickness, clonedInner.Thickness);
        }

        [Fact]
        public void CloneForInsertionRewritesOnlyReferencesToActionsInsideCopiedSubtree()
        {
            var first = new MeasureNode { Key = "measurement" };
            var move = new MoveNode
            {
                Key = "move_1",
                MoveX = ParameterBinding.Expression(
                    "measurement.result.distance + external.result.offset + 'measurement.result.literal'")
            };
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Condition = ParameterBinding.Expression(
                "move_1.parameters.move_x > 0 && move_1 != null && external.result.ready");
            conditional.Branches[0].Body.Add(first);
            conditional.Branches[0].Body.Add(move);

            var clone = Assert.IsType<ConditionalNode>(WorkflowNodeCopy.CloneForInsertion(
                conditional,
                new[] { "choice", "measurement", "move_1" }));

            var clonedNodes = clone.Branches[0].Body.ToArray();
            var clonedMove = Assert.IsType<MoveNode>(clonedNodes[1]);
            Assert.Equal(
                "=measurement_copy.result.distance + external.result.offset + 'measurement.result.literal'",
                clonedMove.MoveX.RawText);
            Assert.Equal(
                "=move_1_copy.parameters.move_x > 0 && move_1_copy != null && external.result.ready",
                clone.Branches[0].Condition!.RawText);
            Assert.NotEqual(conditional.Branches[0].Id, clone.Branches[0].Id);
        }

        [Fact]
        public void CloneManyForInsertionPreservesOrderAndRewritesReferencesAcrossSelectedRoots()
        {
            var measure = new MeasureNode { Key = "measure_1" };
            var drill = new DrillNode
            {
                Key = "drill_1",
                Thickness = ParameterBinding.Expression(
                    "measure_1.result.measured_distance + external.parameters.offset")
            };

            var clones = WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { measure, drill },
                new[] { "measure_1", "drill_1" });

            var clonedMeasure = Assert.IsType<MeasureNode>(clones[0]);
            var clonedDrill = Assert.IsType<DrillNode>(clones[1]);
            Assert.Equal("measure_1_copy", clonedMeasure.Key);
            Assert.Equal("drill_1_copy", clonedDrill.Key);
            Assert.Equal(
                "=measure_1_copy.result.measured_distance + external.parameters.offset",
                clonedDrill.Thickness.RawText);
            Assert.NotEqual(measure.Id, clonedMeasure.Id);
            Assert.NotEqual(drill.Id, clonedDrill.Id);
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
                new WorkflowNode[] { new MoveNode(), null! },
                Array.Empty<string>()));

            var repeatedRoot = new MeasureNode { Key = "measure_1" };
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { repeatedRoot, repeatedRoot },
                Array.Empty<string>()));

            var child = new DelayNode { Key = "delay_child" };
            var parent = new RepeatNode { Key = "repeat_parent" };
            parent.Body.Add(child);
            Assert.Throws<ArgumentException>(() => WorkflowNodeCopy.CloneManyForInsertion(
                new WorkflowNode[] { parent, child },
                Array.Empty<string>()));

            var duplicateAlias = new AbortNode { Key = "MEASURE_1" };
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
            var first = new MeasureNode { Key = "Action" };
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
            var seed = new MeasureNode { Key = "seed" };
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

            var clonedSeed = Assert.IsType<MeasureNode>(clones[0]);
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
