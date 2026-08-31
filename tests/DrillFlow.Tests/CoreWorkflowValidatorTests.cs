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
        public void RequiresCurrentWorkflowSchemaAfterPersistenceMigration()
        {
            var document = Document(ValidStage("stage_1"));
            document.SchemaVersion = 1;

            AssertInvalid(document, "document.schema_version", "schemaVersion");
        }

        [Fact]
        public void AcceptsValidEquipmentActionsNestedFlowAndPreviousReferences()
        {
            var stage = ValidStage("stage_1");
            stage.MoveMode = ParameterBinding.Literal("absolute");
            stage.StageX = ParameterBinding.Literal("-8.2E3");
            var camera = new CameraNode
            {
                Key = "camera_1",
                MoveMode = ParameterBinding.Literal("relative"),
                CameraX = ParameterBinding.Expression("stage_1.parameters.stage_x"),
                CameraY = ParameterBinding.Literal("7.62E-6")
            };
            var focus = new FocusNode { Key = "focus_1" };
            var inside = new DelayNode
            {
                Key = "inside_delay",
                DurationMilliseconds = ParameterBinding.Literal("20")
            };
            var repeat = new RepeatNode
            {
                Key = "repeat_1",
                Count = ParameterBinding.Literal(int.MaxValue.ToString())
            };
            repeat.Body.Add(inside);
            var integration = new IntegrationNode
            {
                Key = "integration_1",
                HorizontalFieldWidth = ParameterBinding.Expression("focus_1.parameters.hfw"),
                FrameCount = ParameterBinding.Literal("64"),
                ImagePath = ParameterBinding.Literal(@"\\server\images\integrated.png")
            };
            var live = new LiveNode
            {
                Key = "live_1",
                ImagePath = ParameterBinding.Literal(@"C:\images\live.png")
            };
            var om = new OmNode
            {
                Key = "om_1",
                ImagePath = ParameterBinding.Literal(@"C:\images\om.bmp")
            };
            var lens = new LensNode
            {
                Key = "lens_1",
                LensMode = ParameterBinding.Literal("lens2")
            };
            var autoContrastBrightness = new AutoContrastBrightnessNode
            {
                Key = "acb_1",
                HorizontalFieldWidth = ParameterBinding.Expression("focus_1.parameters.hfw")
            };
            var conditional = new ConditionalNode { Key = "if_1" };
            conditional.Branches[0].Condition =
                ParameterBinding.Expression("inside_delay.parameters.milliseconds >= 0");
            conditional.Branches[0].Body.Add(new AbortNode { Key = "branch_abort" });
            conditional.Branches.Add(new ConditionalBranch
            {
                Kind = ConditionalBranchKind.Else,
                Condition = null
            });

            var result = _validator.Validate(
                Document(
                    stage,
                    camera,
                    focus,
                    repeat,
                    integration,
                    live,
                    om,
                    lens,
                    autoContrastBrightness,
                    conditional));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(x => x.Message)));
            Assert.Empty(result.Issues);
        }

        [Theory]
        [InlineData("-1E100")]
        [InlineData("0")]
        [InlineData("1E100")]
        public void CoordinatesAllowAnyFiniteSignedValueInBothMoveModes(string coordinate)
        {
            foreach (var mode in new[] { "absolute", "relative" })
            {
                var stage = ValidStage("stage_1");
                stage.MoveMode = ParameterBinding.Literal(mode);
                stage.StageX = ParameterBinding.Literal(coordinate);
                var camera = new CameraNode
                {
                    Key = "camera_1",
                    MoveMode = ParameterBinding.Literal(mode),
                    CameraX = ParameterBinding.Literal(coordinate),
                    CameraY = ParameterBinding.Literal("0")
                };

                Assert.True(_validator.Validate(Document(stage, camera)).IsValid);
            }
        }

        [Theory]
        [InlineData("1E999")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        public void CoordinatesRejectNonFiniteValues(string coordinate)
        {
            var stage = ValidStage("stage_1");
            stage.StageX = ParameterBinding.Literal(coordinate);

            AssertInvalid(Document(stage), "parameter.range_or_type", ".stageX");
        }

        [Theory]
        [InlineData("")]
        [InlineData("sideways")]
        [InlineData("1")]
        public void StageAndCameraRejectUnknownMoveModes(string mode)
        {
            var stage = ValidStage("stage_1");
            stage.MoveMode = ParameterBinding.Literal(mode);
            var camera = new CameraNode
            {
                Key = "camera_1",
                MoveMode = ParameterBinding.Literal(mode)
            };

            var result = _validator.Validate(Document(stage, camera));

            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == stage.Id && issue.Path.EndsWith(".moveMode"));
            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == camera.Id && issue.Path.EndsWith(".moveMode"));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1E-6")]
        [InlineData("2.4E-3")]
        [InlineData("2.400001E-3")]
        [InlineData("Infinity")]
        public void RejectsHorizontalFieldWidthOutsideStrictRange(string hfw)
        {
            var focus = new FocusNode
            {
                Key = "focus_1",
                HorizontalFieldWidth = ParameterBinding.Literal(hfw)
            };

            AssertInvalid(Document(focus), "parameter.range_or_type", ".horizontalFieldWidth");
        }

        [Fact]
        public void AcceptsHorizontalFieldWidthImmediatelyBelowUpperLimit()
        {
            var focus = new FocusNode
            {
                Key = "focus_1",
                HorizontalFieldWidth = ParameterBinding.Literal("2.399999E-3")
            };

            Assert.True(_validator.Validate(Document(focus)).IsValid);
        }

        [Theory]
        [InlineData("0", "13", ".range")]
        [InlineData("-1E-6", "13", ".range")]
        [InlineData("Infinity", "13", ".range")]
        [InlineData("1E-6", "3", ".steps")]
        [InlineData("1E-6", "4.5", ".steps")]
        [InlineData("1E-6", "2147483648", ".steps")]
        public void RejectsInvalidFocusRangeAndSteps(string range, string steps, string path)
        {
            var focus = new FocusNode
            {
                Key = "focus_1",
                Range = ParameterBinding.Literal(range),
                Steps = ParameterBinding.Literal(steps)
            };

            AssertInvalid(Document(focus), "parameter.range_or_type", path);
        }

        [Theory]
        [InlineData("1")]
        [InlineData("2")]
        [InlineData("4")]
        [InlineData("8")]
        [InlineData("16")]
        [InlineData("32")]
        [InlineData("64")]
        public void AcceptsIntegrationPowerOfTwoFrameCountsThrough64(string count)
        {
            var integration = new IntegrationNode
            {
                Key = "integration_1",
                FrameCount = ParameterBinding.Literal(count)
            };

            Assert.True(_validator.Validate(Document(integration)).IsValid);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("3")]
        [InlineData("63")]
        [InlineData("65")]
        [InlineData("128")]
        [InlineData("1.5")]
        public void RejectsInvalidIntegrationFrameCounts(string count)
        {
            var integration = new IntegrationNode
            {
                Key = "integration_1",
                FrameCount = ParameterBinding.Literal(count)
            };

            AssertInvalid(Document(integration), "parameter.range_or_type", ".frameCount");
        }

        [Theory]
        [InlineData("0")]
        [InlineData("2")]
        [InlineData("1.5")]
        public void LiveFrameCountMustBeExactlyOne(string count)
        {
            var live = new LiveNode
            {
                Key = "live_1",
                FrameCount = ParameterBinding.Literal(count)
            };

            AssertInvalid(Document(live), "parameter.range_or_type", ".frameCount");
        }

        [Theory]
        [InlineData(@"C:\images\one.png")]
        [InlineData(@"Z:\shared\one.tif")]
        [InlineData(@"\\server\share\one.bmp")]
        public void AcceptsAbsoluteLocalMappedAndUncImagePaths(string path)
        {
            var integration = new IntegrationNode
            {
                Key = "integration_1",
                ImagePath = ParameterBinding.Literal(path)
            };

            Assert.True(_validator.Validate(Document(integration)).IsValid);
        }

        [Theory]
        [InlineData("")]
        [InlineData(@"relative\one.png")]
        [InlineData(@"C:\images\")]
        [InlineData(@"\\server\share")]
        [InlineData(@"C:\images\.")]
        [InlineData(@"\\server\share\..")]
        [InlineData(@"C:\images\trailing.")]
        [InlineData(@"C:\images\bad?.png")]
        public void RejectsImagePathsThatAreNotAbsoluteWindowsFilenames(string path)
        {
            var integration = new IntegrationNode
            {
                Key = "integration_1",
                ImagePath = ParameterBinding.Literal(path)
            };

            AssertInvalid(Document(integration), "parameter.range_or_type", ".imagePath");
        }

        [Fact]
        public void NewEquipmentActionsUseContractDefaults()
        {
            var om = new OmNode();
            var lens = new LensNode();
            var autoContrastBrightness = new AutoContrastBrightnessNode();

            Assert.Equal(@"C:\DrillFlow\Images\om.bmp", om.ImagePath.RawText);
            Assert.Equal("no_change", lens.LensMode.RawText);
            Assert.Equal("2.04E-6", autoContrastBrightness.HorizontalFieldWidth.RawText);
            Assert.True(_validator.Validate(Document(om, lens, autoContrastBrightness)).IsValid);
        }

        [Theory]
        [InlineData("lens1", LensMode.Lens1)]
        [InlineData(" LENS2 ", LensMode.Lens2)]
        [InlineData("no_change", LensMode.NoChange)]
        public void AcceptsSupportedLensModes(string rawText, LensMode expected)
        {
            var node = new LensNode { LensMode = ParameterBinding.Literal(rawText) };

            Assert.Equal(
                expected,
                ParameterValueValidator.GetLensMode(ExpressionValue.String(rawText)));
            Assert.True(_validator.Validate(Document(node)).IsValid);
        }

        [Theory]
        [InlineData("")]
        [InlineData("lens3")]
        [InlineData("nochange")]
        [InlineData("1")]
        public void RejectsUnsupportedLensModes(string rawText)
        {
            var node = new LensNode { LensMode = ParameterBinding.Literal(rawText) };

            AssertInvalid(Document(node), "parameter.range_or_type", ".lensMode");
        }

        [Fact]
        public void OmAndAutoContrastBrightnessReuseImagePathAndHfwValidation()
        {
            var om = new OmNode { ImagePath = ParameterBinding.Literal(@"relative\om.bmp") };
            var autoContrastBrightness = new AutoContrastBrightnessNode
            {
                HorizontalFieldWidth = ParameterBinding.Literal("2.4E-3")
            };

            var result = _validator.Validate(Document(om, autoContrastBrightness));

            Assert.Contains(result.Issues, issue => issue.NodeId == om.Id && issue.Path.EndsWith(".imagePath"));
            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == autoContrastBrightness.Id
                         && issue.Path.EndsWith(".horizontalFieldWidth"));
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
            var badFocus = new FocusNode
            {
                Key = "focus_1",
                HorizontalFieldWidth = ParameterBinding.Expression("1.2E-3 + 1.2E-3")
            };
            var badConditional = new ConditionalNode { Key = "if_1" };
            badConditional.Branches[0].Condition = ParameterBinding.Expression("1 + 2");

            var result = _validator.Validate(Document(badFocus, badConditional));

            Assert.Contains(
                result.Issues,
                x => x.Path.EndsWith(".horizontalFieldWidth") && x.Code == "parameter.range_or_type");
            var conditionIssue = Assert.Single(
                result.Issues,
                x => x.Path.EndsWith(".condition") && x.Code == "parameter.range_or_type");
            Assert.Equal(badConditional.Id, conditionIssue.NodeId);
        }

        [Fact]
        public void KeysAndIdsMustBeNonEmptyValidAndUniqueCaseInsensitively()
        {
            var first = ValidStage("action_1");
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
            var first = ValidStage("first");
            var validPrevious = ValidStage("valid_previous");
            validPrevious.StageX = ParameterBinding.Expression("first.parameters.stage_x + 1E-3");
            var futureReference = ValidStage("future_reference");
            futureReference.StageX = ParameterBinding.Expression("later.parameters.stage_x");
            var selfReference = ValidStage("self_reference");
            selfReference.StageX = ParameterBinding.Expression("self_reference.parameters.stage_x");
            var unknownReference = ValidStage("unknown_reference");
            unknownReference.StageX = ParameterBinding.Expression("does_not_exist.result.value");
            var later = ValidStage("later");

            var result = _validator.Validate(Document(
                first,
                validPrevious,
                futureReference,
                selfReference,
                unknownReference,
                later));

            Assert.DoesNotContain(result.Issues, x => x.NodeId == validPrevious.Id);
            Assert.Contains(
                result.Issues,
                x => x.NodeId == futureReference.Id && x.Code == "expression.reference_not_previous");
            Assert.Contains(
                result.Issues,
                x => x.NodeId == selfReference.Id && x.Code == "expression.reference_not_previous");
            Assert.Contains(
                result.Issues,
                x => x.NodeId == unknownReference.Id && x.Code == "expression.unknown_reference");
        }

        [Fact]
        public void DisabledActionsAreNotGuaranteedExpressionReferences()
        {
            var disabled = new FocusNode { Key = "disabled_focus", IsEnabled = false };
            var after = ValidStage("after");
            after.StageX = ParameterBinding.Expression("disabled_focus.result.z_to_sharpness_2d[0][0]");

            var result = _validator.Validate(Document(disabled, after));

            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == after.Id && issue.Code == "expression.reference_not_previous");
        }

        [Fact]
        public void SelectedActionValidationIgnoresUnrelatedInvalidActions()
        {
            var unrelated = new FocusNode
            {
                Key = "invalid_focus",
                HorizontalFieldWidth = ParameterBinding.Literal("0")
            };
            var selected = ValidStage("selected_stage");
            var document = Document(unrelated, selected);

            Assert.False(_validator.Validate(document).IsValid);
            Assert.True(_validator.ValidateSelectedAction(document, selected.Id).IsValid);
        }

        [Fact]
        public void SelectedActionValidationStillRejectsTheSelectedSubtree()
        {
            var selected = ValidStage("selected_stage");
            selected.StageX = ParameterBinding.Literal("not-a-number");

            var result = _validator.ValidateSelectedAction(Document(selected), selected.Id);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == selected.Id && issue.Code == "parameter.range_or_type");
        }

        [Fact]
        public void SelectedActionValidationRequiresReferencedRuntimeResult()
        {
            var source = ValidStage("source_stage");
            var selected = new IntegrationNode
            {
                Key = "selected_integration",
                HorizontalFieldWidth = ParameterBinding.Expression(
                    "source_stage.result.current_stage_x")
            };
            var document = Document(source, selected);

            var unavailable = _validator.ValidateSelectedAction(document, selected.Id);
            var available = _validator.ValidateSelectedAction(
                document,
                selected.Id,
                new[] { source.Id });

            Assert.Contains(
                unavailable.Issues,
                issue => issue.NodeId == selected.Id
                         && issue.Code == "expression.result_unavailable");
            Assert.True(available.IsValid);
        }

        [Fact]
        public void SelectedActionValidationIncludesReferencedParameterActionErrors()
        {
            var source = ValidStage("source_stage");
            source.StageX = ParameterBinding.Literal("not-a-number");
            var selected = ValidStage("selected_stage");
            selected.StageX = ParameterBinding.Expression("source_stage.parameters.stage_x");

            var result = _validator.ValidateSelectedAction(
                Document(source, selected),
                selected.Id);

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == source.Id && issue.Code == "parameter.range_or_type");
        }

        [Fact]
        public void RejectsUnknownTopLevelActionMembersButAllowsDynamicResultFields()
        {
            var first = ValidStage("first");
            var typo = ValidStage("typo");
            typo.StageX = ParameterBinding.Expression("first.paramters.stage_x");
            var dynamicResult = ValidStage("dynamic_result");
            dynamicResult.StageX = ParameterBinding.Expression("first.result.equipment_defined_value");

            var result = _validator.Validate(Document(first, typo, dynamicResult));

            Assert.Contains(
                result.Issues,
                issue => issue.NodeId == typo.Id && issue.Code == "expression.unknown_action_member");
            Assert.DoesNotContain(result.Issues, issue => issue.NodeId == dynamicResult.Id);
        }

        [Fact]
        public void RepeatBodyResultsAreAvailableAfterAtLeastOneIteration()
        {
            var inner = new FocusNode { Key = "inner_focus" };
            var repeat = new RepeatNode { Key = "repeat_1", Count = ParameterBinding.Literal("1") };
            repeat.Body.Add(inner);
            var after = ValidStage("after");
            after.StageX = ParameterBinding.Expression("inner_focus.results.last.z_to_sharpness_2d[0][0]");

            Assert.True(_validator.Validate(Document(repeat, after)).IsValid);
        }

        [Fact]
        public void ConditionalBranchAliasesAreNotGuaranteedAcrossBranchesOrAfterConditional()
        {
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Body.Add(new FocusNode { Key = "if_focus" });
            var elseBranch = new ConditionalBranch { Kind = ConditionalBranchKind.Else, Condition = null };
            var siblingReference = ValidStage("else_stage");
            siblingReference.StageX = ParameterBinding.Expression("if_focus.result.value");
            elseBranch.Body.Add(siblingReference);
            conditional.Branches.Add(elseBranch);
            var after = ValidStage("after");
            after.StageX = ParameterBinding.Expression("if_focus.result.value");

            var result = _validator.Validate(Document(conditional, after));

            Assert.Contains(
                result.Issues,
                x => x.NodeId == siblingReference.Id && x.Code == "expression.reference_not_previous");
            Assert.Contains(
                result.Issues,
                x => x.NodeId == after.Id && x.Code == "expression.reference_not_previous");
        }

        [Fact]
        public void RejectsInvalidConditionalShape()
        {
            var conditional = new ConditionalNode { Key = "choice" };
            conditional.Branches[0].Kind = ConditionalBranchKind.ElseIf;
            conditional.Branches.Add(new ConditionalBranch
            {
                Kind = ConditionalBranchKind.Else,
                Condition = ParameterBinding.Literal("true")
            });
            conditional.Branches.Add(new ConditionalBranch
            {
                Kind = ConditionalBranchKind.ElseIf,
                Condition = ParameterBinding.Literal("true")
            });

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
            Assert.Equal(
                MoveCoordinateMode.Absolute,
                ParameterValueValidator.GetMoveMode(ExpressionValue.String("ABSOLUTE")));
            Assert.Equal(
                MoveCoordinateMode.Relative,
                ParameterValueValidator.GetMoveMode(ExpressionValue.String(" relative ")));
            Assert.Equal(
                -1E100,
                ParameterValueValidator.GetFiniteCoordinate(ExpressionValue.Number(-1E100), "x"),
                12);
            Assert.Equal(
                2.399E-3,
                ParameterValueValidator.GetHorizontalFieldWidth(ExpressionValue.Number(2.399E-3)),
                12);
            Assert.Equal(4, ParameterValueValidator.GetFocusSteps(ExpressionValue.Number(4)));
            Assert.Equal(64, ParameterValueValidator.GetIntegrationFrameCount(ExpressionValue.Number(64)));
            Assert.Equal(1, ParameterValueValidator.GetLiveFrameCount(ExpressionValue.Number(1)));
            Assert.Equal(LensMode.NoChange, ParameterValueValidator.GetLensMode(ExpressionValue.String("NO_CHANGE")));
            Assert.Equal(
                @"\\server\share\image.png",
                ParameterValueValidator.GetAbsoluteImagePath(
                    ExpressionValue.String(@" \\server\share\image.png ")));
            Assert.Equal(29999, ParameterValueValidator.GetDelayMilliseconds(ExpressionValue.Number(29999)));
            Assert.Equal(int.MaxValue, ParameterValueValidator.GetRepeatCount(ExpressionValue.Number(int.MaxValue)));
            Assert.Throws<ParameterValidationException>(
                () => ParameterValueValidator.GetHorizontalFieldWidth(ExpressionValue.Number(2.4E-3)));
            Assert.Throws<ParameterValidationException>(
                () => ParameterValueValidator.GetIntegrationFrameCount(ExpressionValue.Number(3)));
        }

        [Fact]
        public void AcceptsHttpDesignerActionAndPreviousDynamicResultReferences()
        {
            var first = new HttpActionNode
            {
                Key = "http_first",
                Method = ParameterBinding.Literal("GET"),
                Url = ParameterBinding.Literal("https://example.test/api"),
                Headers = ParameterBinding.Literal("{\"Accept\":\"application/json\"}"),
                Body = ParameterBinding.Literal(string.Empty),
                TimeoutMilliseconds = ParameterBinding.Literal("300000")
            };
            var second = new HttpActionNode
            {
                Key = "http_second",
                Method = ParameterBinding.Literal("POST"),
                Url = ParameterBinding.Expression("http_first.result.json.links.next"),
                Headers = ParameterBinding.Expression("http_first.result.json.forward_headers"),
                Body = ParameterBinding.Expression("http_first.result.json.payload"),
                TimeoutMilliseconds = ParameterBinding.Literal("1")
            };

            var result = _validator.Validate(Document(first, second));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(x => x.Message)));
        }

        [Fact]
        public void RejectsInvalidHttpMethodUrlHeaderTypeAndTimeout()
        {
            var node = new HttpActionNode
            {
                Key = "http_bad",
                Method = ParameterBinding.Literal("PUT"),
                Url = ParameterBinding.Literal("file:///not-http"),
                Headers = ParameterBinding.Literal("42"),
                TimeoutMilliseconds = ParameterBinding.Literal("300001")
            };

            var result = _validator.Validate(Document(node));

            Assert.False(result.IsValid);
            Assert.Contains(
                result.Issues,
                x => x.Path.EndsWith(".method") && x.Code == "parameter.range_or_type");
            Assert.Contains(
                result.Issues,
                x => x.Path.EndsWith(".url") && x.Code == "parameter.range_or_type");
            Assert.Contains(
                result.Issues,
                x => x.Path.EndsWith(".headers") && x.Code == "parameter.range_or_type");
            Assert.Contains(
                result.Issues,
                x => x.Path.EndsWith(".timeoutMilliseconds") && x.Code == "parameter.range_or_type");
        }

        private static WorkflowDocument Document(params WorkflowNode[] nodes)
        {
            var document = new WorkflowDocument { Name = "Test workflow" };
            document.Nodes.AddRange(nodes);
            return document;
        }

        private static StageNode ValidStage(string key)
        {
            return new StageNode
            {
                Key = key,
                MoveMode = ParameterBinding.Literal("relative"),
                StageX = ParameterBinding.Literal("0"),
                StageY = ParameterBinding.Literal("0")
            };
        }

        private void AssertInvalid(WorkflowDocument document, string code, string pathSuffix)
        {
            var result = _validator.Validate(document);
            Assert.False(result.IsValid);
            Assert.Contains(
                result.Issues,
                x => x.Code == code && x.Path.EndsWith(pathSuffix, StringComparison.Ordinal));
        }
    }
}
