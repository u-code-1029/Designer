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
    }
}
