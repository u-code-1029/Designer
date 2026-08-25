using System;
using System.Linq;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Validation;
using DrillFlow.Core.Workflows;
using Xunit;

namespace DrillFlow.Tests
{
    public sealed class CoreWorkflowValidatorTests
    {
        private readonly WorkflowValidator _validator = new WorkflowValidator();

        [Fact]
        public void AcceptsValidNestedWorkflowAndPreviousReferences()
        {
            var move = ValidMove("move_1");
            move.MoveMode = ParameterBinding.Literal("absolute");
            move.MoveX = ParameterBinding.Literal("-4.99E-1");
            var measure = new MeasureNode { Key = "measure_1" };
            var inside = new DelayNode
            {
                Key = "inside_delay",
                DurationMilliseconds = ParameterBinding.Literal("20")
            };
            var repeat = new RepeatNode { Key = "repeat_1", Count = ParameterBinding.Literal(int.MaxValue.ToString()) };
            repeat.Body.Add(inside);
            var drill = new DrillNode
            {
                Key = "drill_1",
                Thickness = ParameterBinding.Expression("measure_1.parameters.thickness"),
                DrillResultPath = ParameterBinding.Literal(@"C:\results\current.csv")
            };
            var conditional = new ConditionalNode { Key = "if_1" };
            conditional.Branches[0].Condition = ParameterBinding.Expression("inside_delay.parameters.milliseconds >= 0");
            conditional.Branches[0].Body.Add(new AbortNode { Key = "branch_abort" });
            conditional.Branches.Add(new ConditionalBranch
            {
                Kind = ConditionalBranchKind.Else,
                Condition = null
            });
            var document = Document(move, measure, repeat, drill, conditional);

            var result = _validator.Validate(document);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(x => x.Message)));
            Assert.Empty(result.Issues);
        }

        [Theory]
        [InlineData("-0.5")]
        [InlineData("0.5")]
        [InlineData("1E999")]
        [InlineData("NaN")]
        public void RejectsMoveCoordinatesAtOrOutsideExclusiveLimits(string coordinate)
        {
            var move = ValidMove("move_1");
            move.MoveX = ParameterBinding.Literal(coordinate);

            AssertInvalid(Document(move), "parameter.range_or_type", ".moveX");
        }

        [Theory]
        [InlineData("-0.499999")]
        [InlineData("0")]
        [InlineData("0.499999")]
        public void AllowsNegativeAndPositiveCoordinatesForAbsoluteAndRelativeModes(string coordinate)
        {
            foreach (var mode in new[] { "absolute", "relative" })
            {
                var move = ValidMove("move_1");
                move.MoveMode = ParameterBinding.Literal(mode);
                move.MoveX = ParameterBinding.Literal(coordinate);
                Assert.True(_validator.Validate(Document(move)).IsValid);
            }
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1E-3")]
        [InlineData("2.400001E-3")]
        [InlineData("Infinity")]
        public void RejectsThicknessOutsideConfiguredRange(string thickness)
        {
            var measure = new MeasureNode { Key = "measure_1", Thickness = ParameterBinding.Literal(thickness) };

            AssertInvalid(Document(measure), "parameter.range_or_type", ".thickness");
        }

        [Fact]
        public void AcceptsMaximumThickness()
        {
            var measure = new MeasureNode { Key = "measure_1", Thickness = ParameterBinding.Literal("2.4E-3") };

            Assert.True(_validator.Validate(Document(measure)).IsValid);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("30000")]
        [InlineData("1.5")]
        public void RejectsDelayOutsideZeroThrough29999IntegerRange(string delay)
        {
            var node = new DelayNode { Key = "delay_1", DurationMilliseconds = ParameterBinding.Literal(delay) };

            AssertInvalid(Document(node), "parameter.range_or_type", ".durationMilliseconds");
        }

        [Theory]
        [InlineData("0")]
        [InlineData("2147483648")]
        [InlineData("2.5")]
        public void RejectsRepeatCountOutsideInt32PositiveRange(string count)
        {
            var node = new RepeatNode { Key = "repeat_1", Count = ParameterBinding.Literal(count) };

            AssertInvalid(Document(node), "parameter.range_or_type", ".count");
        }

        [Fact]
        public void ConstantExpressionsReceiveTheSameRangeAndTypeValidation()
        {
            var badMove = ValidMove("move_1");
            badMove.MoveX = ParameterBinding.Expression("0.25 + 0.25");
            var badConditional = new ConditionalNode { Key = "if_1" };
            badConditional.Branches[0].Condition = ParameterBinding.Expression("1 + 2");

            var result = _validator.Validate(Document(badMove, badConditional));

            Assert.Contains(result.Issues, x => x.Path.EndsWith(".moveX") && x.Code == "parameter.range_or_type");
            Assert.Contains(result.Issues, x => x.Path.EndsWith(".condition") && x.Code == "parameter.range_or_type");
        }

        [Fact]
        public void KeysAndIdsMustBeNonEmptyValidAndUniqueCaseInsensitively()
        {
            var first = ValidMove("action_1");
            var duplicate = new DelayNode { Key = "ACTION_1", Id = first.Id };
            var reserved = new AbortNode { Key = "true" };
            var malformed = new AbortNode { Key = "1 bad-key" };
            var emptyId = new AbortNode { Key = "valid_key", Id = Guid.Empty };

            var result = _validator.Validate(Document(first, duplicate, reserved, malformed, emptyId));

            Assert.Contains(result.Issues, x => x.Code == "node.duplicate_key");
            Assert.Contains(result.Issues, x => x.Code == "node.duplicate_id");
            Assert.Equal(2, result.Issues.Count(x => x.Code == "node.key_format"));
            Assert.Contains(result.Issues, x => x.Code == "node.id");
        }

        [Fact]
        public void ReferencesMustTargetKnownGuaranteedPreviousActions()
        {
            var first = ValidMove("first");
            var validPrevious = ValidMove("valid_previous");
            validPrevious.MoveX = ParameterBinding.Expression("first.parameters.move_x + 1E-3");
            var futureReference = ValidMove("future_reference");
            futureReference.MoveX = ParameterBinding.Expression("later.parameters.move_x");
            var selfReference = ValidMove("self_reference");
            selfReference.MoveX = ParameterBinding.Expression("self_reference.parameters.move_x");
            var unknownReference = ValidMove("unknown_reference");
            unknownReference.MoveX = ParameterBinding.Expression("does_not_exist.result.value");
            var later = ValidMove("later");

            var result = _validator.Validate(Document(
                first,
                validPrevious,
                futureReference,
                selfReference,
                unknownReference,
                later));

            Assert.DoesNotContain(result.Issues, x => x.NodeId == validPrevious.Id);
            Assert.Contains(result.Issues, x => x.NodeId == futureReference.Id && x.Code == "expression.reference_not_previous");
            Assert.Contains(result.Issues, x => x.NodeId == selfReference.Id && x.Code == "expression.reference_not_previous");
            Assert.Contains(result.Issues, x => x.NodeId == unknownReference.Id && x.Code == "expression.unknown_reference");
        }

        [Fact]
        public void DisabledActionsAreNotGuaranteedExpressionReferences()
        {
            var disabled = new MeasureNode
            {
                Key = "disabled_measure",
                IsEnabled = false,
                Thickness = ParameterBinding.Literal("1E-3")
            };
            var after = ValidMove("after");
            after.MoveX = ParameterBinding.Expression("disabled_measure.result.measured_distance");

            var result = _validator.Validate(Document(disabled, after));

            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == after.Id && issue.Code == "expression.reference_not_previous");
        }

        [Fact]
        public void RejectsUnknownTopLevelActionMembersButAllowsDynamicResultFields()
        {
            var first = ValidMove("first");
            var typo = ValidMove("typo");
            typo.MoveX = ParameterBinding.Expression("first.paramters.move_x");
            var dynamicResult = ValidMove("dynamic_result");
            dynamicResult.MoveX = ParameterBinding.Expression("first.result.equipment_defined_value");

            var result = _validator.Validate(Document(first, typo, dynamicResult));

            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == typo.Id && issue.Code == "expression.unknown_action_member");
            Assert.DoesNotContain(result.Issues, issue => issue.NodeId == dynamicResult.Id);
        }

        [Fact]
        public void RepeatBodyResultsAreAvailableAfterAtLeastOneIteration()
        {
            var inner = new MeasureNode { Key = "inner_measure" };
            var repeat = new RepeatNode { Key = "repeat_1", Count = ParameterBinding.Literal("1") };
            repeat.Body.Add(inner);
            var after = ValidMove("after");
            after.MoveX = ParameterBinding.Expression("inner_measure.results.last.measured_distance");

            Assert.True(_validator.Validate(Document(repeat, after)).IsValid);
        }

        [Fact]
        public void ConditionalBranchAliasesAreNotGuaranteedAcrossBranchesOrAfterConditional()
        {
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Body.Add(new MeasureNode { Key = "if_measure" });
            var elseBranch = new ConditionalBranch { Kind = ConditionalBranchKind.Else, Condition = null };
            var siblingReference = ValidMove("else_move");
            siblingReference.MoveX = ParameterBinding.Expression("if_measure.result.measured_distance");
            elseBranch.Body.Add(siblingReference);
            conditional.Branches.Add(elseBranch);
            var after = ValidMove("after");
            after.MoveX = ParameterBinding.Expression("if_measure.result.measured_distance");

            var result = _validator.Validate(Document(conditional, after));

            Assert.Contains(result.Issues, x => x.NodeId == siblingReference.Id && x.Code == "expression.reference_not_previous");
            Assert.Contains(result.Issues, x => x.NodeId == after.Id && x.Code == "expression.reference_not_previous");
        }

        [Fact]
        public void RejectsInvalidConditionalShape()
        {
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Kind = ConditionalBranchKind.ElseIf;
            conditional.Branches.Add(new ConditionalBranch { Kind = ConditionalBranchKind.Else, Condition = ParameterBinding.Literal("true") });
            conditional.Branches.Add(new ConditionalBranch { Kind = ConditionalBranchKind.ElseIf, Condition = ParameterBinding.Literal("true") });

            var result = _validator.Validate(Document(conditional));

            Assert.Contains(result.Issues, x => x.Code == "conditional.first_branch");
            Assert.Contains(result.Issues, x => x.Code == "conditional.else_position");
            Assert.Contains(result.Issues, x => x.Code == "conditional.else_condition");
        }

        [Fact]
        public void DetectsContainerCyclesWithoutRecursingForever()
        {
            var repeat = new RepeatNode { Key = "repeat_1" };
            repeat.Body.Add(repeat);

            var result = _validator.Validate(Document(repeat));

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, x => x.Code == "structure.cycle");
        }

        [Fact]
        public void RuntimeValueRulesGuardEvaluatedExpressionsBeforeRequestCreation()
        {
            Assert.Equal(MoveCoordinateMode.Absolute, ParameterValueValidator.GetMoveMode(ExpressionValue.String("ABSOLUTE")));
            Assert.Equal(-0.499, ParameterValueValidator.GetMoveCoordinate(ExpressionValue.Number(-0.499), "x"), 12);
            Assert.Equal(0.0024, ParameterValueValidator.GetThickness(ExpressionValue.Number(0.0024)), 12);
            Assert.Equal(29999, ParameterValueValidator.GetDelayMilliseconds(ExpressionValue.Number(29999)));
            Assert.Equal(int.MaxValue, ParameterValueValidator.GetRepeatCount(ExpressionValue.Number(int.MaxValue)));
            Assert.Throws<ParameterValidationException>(() => ParameterValueValidator.GetMoveCoordinate(ExpressionValue.Number(-0.5), "x"));
        }

        private static WorkflowDocument Document(params WorkflowNode[] nodes)
        {
            var document = new WorkflowDocument { Name = "Test workflow" };
            document.Nodes.AddRange(nodes);
            return document;
        }

        private static MoveNode ValidMove(string key)
        {
            return new MoveNode
            {
                Key = key,
                MoveMode = ParameterBinding.Literal("relative"),
                MoveX = ParameterBinding.Literal("0"),
                MoveY = ParameterBinding.Literal("0")
            };
        }

        private void AssertInvalid(WorkflowDocument document, string code, string pathSuffix)
        {
            var result = _validator.Validate(document);
            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, x => x.Code == code && x.Path.EndsWith(pathSuffix, StringComparison.Ordinal));
        }
    }
}
