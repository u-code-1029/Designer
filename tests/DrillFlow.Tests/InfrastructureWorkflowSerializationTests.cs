using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Core.Workflows;
using DrillFlow.Infrastructure.Persistence;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureWorkflowSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesSchemaNestedTypesIdsAndRawExpressions()
    {
        const string expression = "  =focus_1.parameters.range + 2.50E-4  ";
        var nestedStageId = Guid.NewGuid();
        var document = new WorkflowDocument
        {
            Name = "Nested workflow",
            Description = "serialization test",
            Nodes = new List<WorkflowNode>
            {
                new FocusNode { Key = "focus_1" },
                new RepeatNode
                {
                    Key = "repeat_1",
                    Count = new ParameterBinding("2147483647"),
                    Body = new List<WorkflowNode>
                    {
                        new StageNode
                        {
                            Id = nestedStageId,
                            Key = "stage_1",
                            StageX = new ParameterBinding(expression),
                            StageY = new ParameterBinding("-1E-3"),
                        },
                        new ConditionalNode
                        {
                            Key = "if_1",
                            Branches = new List<ConditionalBranch>
                            {
                                new()
                                {
                                    Kind = ConditionalBranchKind.If,
                                    Condition = new ParameterBinding("=stage_1.parameters.stage_x < 0"),
                                    Body = new List<WorkflowNode>
                                    {
                                        new IntegrationNode
                                        {
                                            Key = "integration_1",
                                            ImagePath = new ParameterBinding(@"C:\results\one.png"),
                                        },
                                    },
                                },
                                new()
                                {
                                    Kind = ConditionalBranchKind.Else,
                                    Condition = null,
                                    Body = new List<WorkflowNode> { new AbortNode { Key = "abort_1" } },
                                },
                            },
                        },
                    },
                },
            },
        };

        var serializer = new JsonWorkflowDocumentSerializer();
        var json = serializer.Serialize(document);
        var restored = serializer.Deserialize(json);

        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"stage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"focus\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"integration\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isExpression", json, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(WorkflowDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.IsType<FocusNode>(restored.Nodes[0]);
        var repeat = Assert.IsType<RepeatNode>(restored.Nodes[1]);
        var stage = Assert.IsType<StageNode>(repeat.Body[0]);
        Assert.Equal(nestedStageId, stage.Id);
        Assert.Equal(expression, stage.StageX.RawText);
        var conditional = Assert.IsType<ConditionalNode>(repeat.Body[1]);
        Assert.IsType<IntegrationNode>(Assert.Single(conditional.Branches[0].Body));
        Assert.IsType<AbortNode>(Assert.Single(conditional.Branches[1].Body));
        Assert.Null(conditional.Branches[1].Condition);
    }

    [Fact]
    public void RoundTrip_PreservesEveryVersion2EquipmentNodeAndBinding()
    {
        WorkflowNode[] nodes =
        {
            new StageNode
            {
                Key = "stage_1",
                MoveMode = ParameterBinding.Literal("absolute"),
                StageX = ParameterBinding.Literal("-3.2E-6"),
                StageY = ParameterBinding.Literal("4.12E-4")
            },
            new CameraNode
            {
                Key = "camera_1",
                MoveMode = ParameterBinding.Literal("relative"),
                CameraX = ParameterBinding.Literal("-1E-6"),
                CameraY = ParameterBinding.Literal("8.2E-3")
            },
            new FocusNode
            {
                Key = "focus_1",
                HorizontalFieldWidth = ParameterBinding.Literal("3.02E-6"),
                Range = ParameterBinding.Literal("50E-6"),
                Steps = ParameterBinding.Literal("13")
            },
            new IntegrationNode
            {
                Key = "integration_1",
                HorizontalFieldWidth = ParameterBinding.Literal("3.02E-6"),
                FrameCount = ParameterBinding.Literal("8"),
                ImagePath = ParameterBinding.Literal(@"\\server\share\integration.png")
            },
            new LiveNode
            {
                Key = "live_1",
                HorizontalFieldWidth = ParameterBinding.Literal("3.02E-6"),
                FrameCount = ParameterBinding.Literal("1"),
                ImagePath = ParameterBinding.Literal(@"C:\images\live.png")
            },
            new OmNode
            {
                Key = "om_1",
                ImagePath = ParameterBinding.Literal(@"C:\images\om.bmp")
            },
            new LensNode
            {
                Key = "lens_1",
                LensMode = ParameterBinding.Literal("lens2")
            },
            new AutoContrastBrightnessNode
            {
                Key = "acb_1",
                HorizontalFieldWidth = ParameterBinding.Literal("2.04E-6")
            },
            new AbortNode { Key = "abort_1" }
        };
        var serializer = new JsonWorkflowDocumentSerializer();

        var json = serializer.Serialize(new WorkflowDocument
        {
            Name = "All equipment actions",
            Nodes = new List<WorkflowNode>(nodes)
        });
        var restored = serializer.Deserialize(json).Nodes;

        Assert.Collection(
            restored,
            node => Assert.IsType<StageNode>(node),
            node => Assert.IsType<CameraNode>(node),
            node => Assert.IsType<FocusNode>(node),
            node => Assert.IsType<IntegrationNode>(node),
            node => Assert.IsType<LiveNode>(node),
            node => Assert.IsType<OmNode>(node),
            node => Assert.IsType<LensNode>(node),
            node => Assert.IsType<AutoContrastBrightnessNode>(node),
            node => Assert.IsType<AbortNode>(node));
        for (var index = 0; index < nodes.Length; index++)
        {
            var expected = nodes[index].GetParameterBindings();
            var actual = restored[index].GetParameterBindings();
            Assert.Equal(expected.Keys, actual.Keys);
            foreach (var key in expected.Keys)
            {
                Assert.Equal(expected[key].RawText, actual[key].RawText);
            }
        }
    }

    [Fact]
    public void Deserialize_MigratesNestedVersion1MoveToStage()
    {
        var documentId = Guid.NewGuid();
        var moveId = Guid.NewGuid();
        var json = "{"
                   + "\"schemaVersion\":1,"
                   + $"\"id\":\"{documentId}\","
                   + "\"name\":\"Legacy move\",\"description\":\"\","
                   + "\"nodes\":[{\"type\":\"repeat\","
                   + "\"count\":{\"rawText\":\"2\"},"
                   + $"\"id\":\"{Guid.NewGuid()}\",\"key\":\"repeat_1\","
                   + "\"displayName\":\"Repeat\",\"isEnabled\":true,\"hasBreakpoint\":false,"
                   + "\"body\":[{\"type\":\"move\","
                   + "\"moveMode\":{\"rawText\":\"absolute\"},"
                   + "\"moveX\":{\"rawText\":\"-3.2E-6\"},"
                   + "\"moveY\":{\"rawText\":\"=move_1.parameters.move_x "
                   + "+ move_1.result.stage_x + move_1.last.stage_y "
                   + "+ move_1.results[0].index + move_1.result.command "
                   + "+ 'move_1.parameters.move_y + move_1.result.stage_x'\"},"
                   + $"\"id\":\"{moveId}\",\"key\":\"move_1\","
                   + "\"displayName\":\"Legacy move\",\"isEnabled\":true,"
                   + "\"hasBreakpoint\":true}]}]}";

        var restored = new JsonWorkflowDocumentSerializer().Deserialize(json);

        Assert.Equal(WorkflowDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(documentId, restored.Id);
        var repeat = Assert.IsType<RepeatNode>(Assert.Single(restored.Nodes));
        var stage = Assert.IsType<StageNode>(Assert.Single(repeat.Body));
        Assert.Equal(moveId, stage.Id);
        Assert.Equal("move_1", stage.Key);
        Assert.Equal("Legacy move", stage.DisplayName);
        Assert.True(stage.HasBreakpoint);
        Assert.Equal("absolute", stage.MoveMode.RawText);
        Assert.Equal("-3.2E-6", stage.StageX.RawText);
        Assert.Equal(
            "=move_1.parameters.stage_x + move_1.result.current_stage_x "
            + "+ move_1.last.current_stage_y + move_1.results[0].correlation_id "
            + "+ move_1.result.type "
            + "+ 'move_1.parameters.move_y + move_1.result.stage_x'",
            stage.StageY.RawText);
    }

    [Theory]
    [InlineData("measure")]
    [InlineData("drill")]
    public void Deserialize_RejectsVersion1EquipmentActionsWithoutSafeMigration(string type)
    {
        var json = "{\"schemaVersion\":1,\"nodes\":[{\"type\":\"" + type + "\"}]}";

        var exception = Assert.Throws<InvalidDataException>(
            () => new JsonWorkflowDocumentSerializer().Deserialize(json));

        Assert.Contains("cannot be migrated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(type, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndLoad_UsesTheSameVersionedRepresentation()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "sample.drillflow.json");
        var serializer = new JsonWorkflowDocumentSerializer();
        var document = new WorkflowDocument
        {
            Name = "Saved",
            Nodes = new List<WorkflowNode> { new DelayNode { Key = "delay_1" } },
        };

        await serializer.SaveAsync(path, document, CancellationToken.None);
        var loaded = await serializer.LoadAsync(path, CancellationToken.None);

        Assert.Equal(WorkflowDocument.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Saved", loaded.Name);
        Assert.IsType<DelayNode>(Assert.Single(loaded.Nodes));
    }

    [Fact]
    public void Deserialize_RejectsUnknownSchemaAndNodeTypes()
    {
        var serializer = new JsonWorkflowDocumentSerializer();

        Assert.Throws<InvalidDataException>(
            () => serializer.Deserialize("{\"schemaVersion\":3,\"nodes\":[]}"));
        Assert.Throws<InvalidDataException>(
            () => serializer.Deserialize("{\"schemaVersion\":2,\"nodes\":[{\"type\":\"plugin\"}]}"));
    }

    [Fact]
    public void RoundTrip_PreservesHttpDesignerActionBindings()
    {
        var serializer = new JsonWorkflowDocumentSerializer();
        var source = new HttpActionNode
        {
            Key = "http_1",
            Method = ParameterBinding.Literal("POST"),
            Url = ParameterBinding.Literal("https://example.test/jobs"),
            Headers = ParameterBinding.Literal("{\"X-Api-Key\":\"secret\"}"),
            Body = ParameterBinding.Expression("stage_1.result.json"),
            TimeoutMilliseconds = ParameterBinding.Literal("45000")
        };

        var json = serializer.Serialize(new WorkflowDocument
        {
            Name = "HTTP",
            Nodes = new List<WorkflowNode> { source }
        });
        var restored = Assert.IsType<HttpActionNode>(Assert.Single(serializer.Deserialize(json).Nodes));

        Assert.Contains("\"type\": \"http\"", json, StringComparison.Ordinal);
        Assert.Equal("POST", restored.Method.RawText);
        Assert.Equal("https://example.test/jobs", restored.Url.RawText);
        Assert.Equal("{\"X-Api-Key\":\"secret\"}", restored.Headers.RawText);
        Assert.Equal("=stage_1.result.json", restored.Body.RawText);
        Assert.Equal("45000", restored.TimeoutMilliseconds.RawText);
    }
}
