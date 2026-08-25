using System;
using System.Linq;
using DrillFlow.Core.Workflows;
using Xunit;

namespace DrillFlow.Tests
{
    public sealed class CoreWorkflowModelTests
    {
        [Fact]
        public void NewNodesHaveStableNonEmptyIdsAndRawBindings()
        {
            var move = new MoveNode
            {
                Key = "move_1",
                MoveX = new ParameterBinding("  =measure_1.result.distance * 2 ")
            };
            var originalId = move.Id;

            Assert.NotEqual(Guid.Empty, originalId);
            Assert.Equal(originalId, move.Id);
            Assert.True(move.MoveX.IsExpression);
            Assert.Equal("measure_1.result.distance * 2", move.MoveX.ExpressionText);
            Assert.Equal("  =measure_1.result.distance * 2 ", move.MoveX.RawText);
            Assert.Equal(WorkflowNodeKind.Move, move.Kind);
        }

        [Fact]
        public void DocumentEnumeratesNestedNodesInDisplayOrder()
        {
            var first = new MoveNode { Key = "first" };
            var inner = new MeasureNode { Key = "inner" };
            var repeat = new RepeatNode { Key = "loop" };
            repeat.Body.Add(inner);
            var branchAction = new DelayNode { Key = "branch_delay" };
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Body.Add(branchAction);
            var document = new WorkflowDocument();
            document.Nodes.Add(first);
            document.Nodes.Add(repeat);
            document.Nodes.Add(conditional);

            var keys = document.EnumerateNodesDepthFirst().Select(x => x.Key).ToArray();

            Assert.Equal(new[] { "first", "loop", "inner", "choice", "branch_delay" }, keys);
            Assert.Same(inner, document.FindNode(inner.Id));
            Assert.Same(branchAction, document.FindNode("BRANCH_DELAY"));
        }

        [Fact]
        public void EveryRequestParameterIsRepresentedByRawBinding()
        {
            WorkflowNode[] nodes =
            {
                new MoveNode(),
                new MeasureNode(),
                new DrillNode(),
                new AbortNode(),
                new DelayNode(),
                new RepeatNode(),
                new ConditionalNode()
            };

            foreach (var binding in nodes.SelectMany(x => x.GetParameterBindings().Values))
            {
                Assert.IsType<ParameterBinding>(binding);
                Assert.NotNull(binding.RawText);
            }

            Assert.Equal(
                new[] { "move_mode", "move_x", "move_y" },
                nodes[0].GetParameterBindings().Keys);
            Assert.Equal(
                new[] { "thickness", "drill_result_path" },
                nodes[2].GetParameterBindings().Keys);
        }
    }
}
