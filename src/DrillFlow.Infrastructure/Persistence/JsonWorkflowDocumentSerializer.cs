using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Persistence;
using DrillFlow.Core.Workflows;
using DrillFlow.Infrastructure.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DrillFlow.Infrastructure.Persistence;

/// <summary>
/// Stores workflow documents with a stable, assembly-name-free node discriminator. Deserialization
/// only permits the known node types, avoiding the security and compatibility problems of
/// TypeNameHandling.
/// </summary>
public sealed class JsonWorkflowDocumentSerializer : IWorkflowDocumentSerializer
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    public string Serialize(WorkflowDocument document)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (document.SchemaVersion != WorkflowDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Cannot save workflow schema version {document.SchemaVersion}; "
                + $"this application writes version {WorkflowDocument.CurrentSchemaVersion}.");
        }

        var serializer = CreatePlainSerializer();
        var root = JObject.FromObject(document, serializer);
        var nodes = new JArray();
        foreach (var node in document.Nodes ?? new List<WorkflowNode>())
        {
            nodes.Add(WriteNode(node, serializer));
        }

        root["nodes"] = nodes;
        return root.ToString(Formatting.Indented);
    }

    public WorkflowDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The workflow document is empty.");
        }

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The workflow document is not valid JSON.", exception);
        }

        var schemaVersion = root["schemaVersion"];
        if (schemaVersion?.Type != JTokenType.Integer)
        {
            throw new InvalidDataException("The workflow document does not contain an integer schemaVersion.");
        }

        var version = schemaVersion.Value<int>();
        if (version != WorkflowDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Workflow schema version {version} is not supported; "
                + $"this application supports version {WorkflowDocument.CurrentSchemaVersion}.");
        }

        var nodeToken = root["nodes"];
        if (nodeToken is not JArray nodeArray)
        {
            throw new InvalidDataException("The workflow document must contain a nodes array.");
        }

        var scalarRoot = (JObject)root.DeepClone();
        scalarRoot.Remove("nodes");

        try
        {
            var serializer = CreatePlainSerializer();
            var document = scalarRoot.ToObject<WorkflowDocument>(serializer)
                ?? throw new InvalidDataException("The workflow document could not be created.");
            document.Nodes = new List<WorkflowNode>();
            foreach (var token in nodeArray)
            {
                document.Nodes.Add(ReadNode(token, serializer));
            }

            return document;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The workflow document has an invalid structure.", exception);
        }
    }

    public async Task SaveAsync(
        string filePath,
        WorkflowDocument document,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A workflow file path is required.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var bytes = Utf8WithoutBom.GetBytes(Serialize(document));
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFilePublisher.PublishCompletedTempFile(tempPath, fullPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public async Task<WorkflowDocument> LoadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A workflow file path is required.", nameof(filePath));
        }

        string json;
        using (var stream = new FileStream(
                   Path.GetFullPath(filePath),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.Asynchronous))
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
        {
            json = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Deserialize(json);
    }

    private static JObject WriteNode(WorkflowNode node, JsonSerializer serializer)
    {
        if (node is null)
        {
            throw new InvalidDataException("A workflow cannot contain a null node.");
        }

        var result = JObject.FromObject(node, serializer);
        result.AddFirst(new JProperty("type", GetNodeTypeName(node)));

        if (node is RepeatNode repeat)
        {
            var body = new JArray();
            foreach (var child in repeat.Body ?? new List<WorkflowNode>())
            {
                body.Add(WriteNode(child, serializer));
            }

            result["body"] = body;
        }
        else if (node is ConditionalNode conditional)
        {
            var branches = new JArray();
            foreach (var branch in conditional.Branches ?? new List<ConditionalBranch>())
            {
                if (branch is null)
                {
                    throw new InvalidDataException("A conditional node cannot contain a null branch.");
                }

                var branchJson = JObject.FromObject(branch, serializer);
                var body = new JArray();
                foreach (var child in branch.Body ?? new List<WorkflowNode>())
                {
                    body.Add(WriteNode(child, serializer));
                }

                branchJson["body"] = body;
                branches.Add(branchJson);
            }

            result["branches"] = branches;
        }

        return result;
    }

    private static WorkflowNode ReadNode(JToken token, JsonSerializer serializer)
    {
        if (token is not JObject nodeJson)
        {
            throw new InvalidDataException("Every workflow node must be a JSON object.");
        }

        var typeName = nodeJson["type"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidDataException("Every workflow node must contain a type discriminator.");
        }

        var scalarNode = (JObject)nodeJson.DeepClone();
        scalarNode.Remove("type");

        switch (typeName)
        {
            case "move":
                return DeserializeNode<MoveNode>(scalarNode, serializer);
            case "measure":
                return DeserializeNode<MeasureNode>(scalarNode, serializer);
            case "drill":
                return DeserializeNode<DrillNode>(scalarNode, serializer);
            case "abort":
                return DeserializeNode<AbortNode>(scalarNode, serializer);
            case "delay":
                return DeserializeNode<DelayNode>(scalarNode, serializer);
            case "repeat":
                return ReadRepeatNode(scalarNode, serializer);
            case "conditional":
                return ReadConditionalNode(scalarNode, serializer);
            default:
                throw new InvalidDataException($"Workflow node type '{typeName}' is not supported.");
        }
    }

    private static RepeatNode ReadRepeatNode(JObject nodeJson, JsonSerializer serializer)
    {
        if (nodeJson["body"] is not JArray bodyJson)
        {
            throw new InvalidDataException("A repeat node must contain a body array.");
        }

        var scalarNode = (JObject)nodeJson.DeepClone();
        scalarNode.Remove("body");
        var repeat = DeserializeNode<RepeatNode>(scalarNode, serializer);
        repeat.Body = new List<WorkflowNode>();
        foreach (var child in bodyJson)
        {
            repeat.Body.Add(ReadNode(child, serializer));
        }

        return repeat;
    }

    private static ConditionalNode ReadConditionalNode(JObject nodeJson, JsonSerializer serializer)
    {
        if (nodeJson["branches"] is not JArray branchArray)
        {
            throw new InvalidDataException("A conditional node must contain a branches array.");
        }

        var scalarNode = (JObject)nodeJson.DeepClone();
        scalarNode.Remove("branches");
        var conditional = DeserializeNode<ConditionalNode>(scalarNode, serializer);
        conditional.Branches = new List<ConditionalBranch>();

        foreach (var branchToken in branchArray)
        {
            if (branchToken is not JObject branchJson || branchJson["body"] is not JArray bodyJson)
            {
                throw new InvalidDataException(
                    "Every conditional branch must be an object containing a body array.");
            }

            var scalarBranch = (JObject)branchJson.DeepClone();
            scalarBranch.Remove("body");
            var branch = scalarBranch.ToObject<ConditionalBranch>(serializer)
                ?? throw new InvalidDataException("A conditional branch could not be created.");
            branch.Body = new List<WorkflowNode>();
            foreach (var child in bodyJson)
            {
                branch.Body.Add(ReadNode(child, serializer));
            }

            conditional.Branches.Add(branch);
        }

        return conditional;
    }

    private static T DeserializeNode<T>(JObject json, JsonSerializer serializer)
        where T : WorkflowNode
    {
        return json.ToObject<T>(serializer)
               ?? throw new InvalidDataException($"A {typeof(T).Name} could not be created.");
    }

    private static string GetNodeTypeName(WorkflowNode node)
    {
        switch (node)
        {
            case MoveNode _:
                return "move";
            case MeasureNode _:
                return "measure";
            case DrillNode _:
                return "drill";
            case AbortNode _:
                return "abort";
            case DelayNode _:
                return "delay";
            case RepeatNode _:
                return "repeat";
            case ConditionalNode _:
                return "conditional";
            default:
                throw new InvalidDataException(
                    $"Workflow node CLR type '{node.GetType().FullName}' is not supported.");
        }
    }

    private static JsonSerializer CreatePlainSerializer()
    {
        return JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new WritableCamelCaseContractResolver(),
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling = NullValueHandling.Include,
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class WritableCamelCaseContractResolver : CamelCasePropertyNamesContractResolver
    {
        protected override JsonProperty CreateProperty(
            System.Reflection.MemberInfo member,
            MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            if (!property.Writable)
            {
                property.Ignored = true;
            }

            return property;
        }
    }
}
