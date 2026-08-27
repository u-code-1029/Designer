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
            var stage = new StageNode
            {
                Key = "stage_1",
                StageX = new ParameterBinding("  =camera_1.result.current_camera_x * 2 ")
            };
            var originalId = stage.Id;

            Assert.NotEqual(Guid.Empty, originalId);
            Assert.Equal(originalId, stage.Id);
            Assert.True(stage.StageX.IsExpression);
            Assert.Equal("camera_1.result.current_camera_x * 2", stage.StageX.ExpressionText);
            Assert.Equal("  =camera_1.result.current_camera_x * 2 ", stage.StageX.RawText);
            Assert.Equal(WorkflowNodeKind.Stage, stage.Kind);
        }

        [Fact]
        public void DocumentEnumeratesNestedNodesInDisplayOrder()
        {
            var first = new StageNode { Key = "first" };
            var inner = new FocusNode { Key = "inner" };
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
                new StageNode(),
                new CameraNode(),
                new FocusNode(),
                new IntegrationNode(),
                new LiveNode(),
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
                new[] { "move_mode", "stage_x", "stage_y" },
                nodes[0].GetParameterBindings().Keys);
            Assert.Equal(
                new[] { "move_mode", "camera_x", "camera_y" },
                nodes[1].GetParameterBindings().Keys);
            Assert.Equal(
                new[] { "hfw", "range", "steps" },
                nodes[2].GetParameterBindings().Keys);
            Assert.Equal(
                new[] { "hfw", "frame_count", "image_path" },
                nodes[3].GetParameterBindings().Keys);
            Assert.Equal(
                new[] { "hfw", "frame_count", "image_path" },
                nodes[4].GetParameterBindings().Keys);
            Assert.Equal(
                "1E-3",
                Assert.IsType<LiveNode>(nodes[4]).HorizontalFieldWidth.RawText);
        }
    }
}
