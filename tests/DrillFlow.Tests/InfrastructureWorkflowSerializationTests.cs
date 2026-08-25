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
        const string expression = "  =measure_1.result.distance + 2.50E-4  ";
        var nestedMoveId = Guid.NewGuid();
        var document = new WorkflowDocument
        {
            Name = "Nested workflow",
            Description = "serialization test",
            Nodes = new List<WorkflowNode>
            {
                new RepeatNode
                {
                    Key = "repeat_1",
                    Count = new ParameterBinding("2147483647"),
                    Body = new List<WorkflowNode>
                    {
                        new MoveNode
                        {
                            Id = nestedMoveId,
                            Key = "move_1",
                            MoveX = new ParameterBinding(expression),
                            MoveY = new ParameterBinding("-1E-3"),
                        },
                        new ConditionalNode
                        {
                            Key = "if_1",
                            Branches = new List<ConditionalBranch>
                            {
                                new()
                                {
                                    Kind = ConditionalBranchKind.If,
                                    Condition = new ParameterBinding("=move_1.parameters.move_x < 0"),
                                    Body = new List<WorkflowNode>
                                    {
                                        new DrillNode
                                        {
                                            Key = "drill_1",
                                            DrillResultPath = new ParameterBinding(@"C:\results\one.csv"),
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

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"repeat\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"conditional\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isExpression", json, StringComparison.OrdinalIgnoreCase);

        var repeat = Assert.IsType<RepeatNode>(Assert.Single(restored.Nodes));
        var move = Assert.IsType<MoveNode>(repeat.Body[0]);
        Assert.Equal(nestedMoveId, move.Id);
        Assert.Equal(expression, move.MoveX.RawText);
        var conditional = Assert.IsType<ConditionalNode>(repeat.Body[1]);
        Assert.IsType<DrillNode>(Assert.Single(conditional.Branches[0].Body));
        Assert.IsType<AbortNode>(Assert.Single(conditional.Branches[1].Body));
        Assert.Null(conditional.Branches[1].Condition);
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

        Assert.Equal("Saved", loaded.Name);
        Assert.IsType<DelayNode>(Assert.Single(loaded.Nodes));
    }

    [Fact]
    public void Deserialize_RejectsUnknownSchemaAndNodeTypes()
    {
        var serializer = new JsonWorkflowDocumentSerializer();

        Assert.Throws<InvalidDataException>(
            () => serializer.Deserialize("{\"schemaVersion\":2,\"nodes\":[]}"));
        Assert.Throws<InvalidDataException>(
            () => serializer.Deserialize("{\"schemaVersion\":1,\"nodes\":[{\"type\":\"plugin\"}]}"));
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
            Body = ParameterBinding.Expression("measure_1.result.json"),
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
        Assert.Equal("=measure_1.result.json", restored.Body.RawText);
        Assert.Equal("45000", restored.TimeoutMilliseconds.RawText);
    }
}
