using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Infrastructure.Communication;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureResponseSimulatorTests
{
    private readonly XmlTemplateEquipmentMessageCodec _codec = new();

    [Fact]
    public async Task CreateDraft_KeepsJsonEditorShapeButReadsActiveXmlRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var request = StageRequest(42);
        File.WriteAllBytes(
            Path.Combine(directory.Path, options.RequestFileName),
            _codec.SerializeRequest(request));
        var simulator = CreateSimulator(options);

        var draft = await simulator.CreateDraftAsync(
            new StageNode(),
            7,
            CancellationToken.None);
        var json = JObject.Parse(draft.Payload);

        Assert.Equal("JSON", simulator.PayloadFormat);
        Assert.Equal("response", json["type"]!.Value<string>());
        Assert.Equal(42, json["correlation_id"]!.Value<int>());
        Assert.Equal("stage", json["action"]!.Value<string>());
        Assert.Equal(0, json["result"]!.Value<int>());
        Assert.Equal(0d, json["current_stage_x"]!.Value<double>());
        Assert.Null(json["image_path"]);
        Assert.Equal(42, draft.ActiveRequest!.CorrelationId);
        Assert.Equal("stage", draft.ActiveRequest.Action);
        Assert.Equal(Path.Combine(directory.Path, options.ResponseFileName), draft.ResponsePath);
    }

    [Fact]
    public async Task CreateDraft_IntegrationUsesGeneratedImageAndActiveRequestValues()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var request = new EquipmentRequestMessage(
            43,
            EquipmentActionNames.Integration,
            new Dictionary<string, object?>
            {
                ["hfw"] = 2E-4,
                ["frame_count"] = 16,
                ["image_path"] = @"C:\Requested\integration.png"
            });
        File.WriteAllBytes(
            Path.Combine(directory.Path, options.RequestFileName),
            _codec.SerializeRequest(request));
        var simulator = CreateSimulator(options);

        var draft = await simulator.CreateDraftAsync(
            new IntegrationNode(),
            null,
            CancellationToken.None,
            @"C:\Generated\preview.png");
        var json = JObject.Parse(draft.Payload);

        Assert.Equal(43, json["correlation_id"]!.Value<int>());
        Assert.Equal(2E-4, json["hfw"]!.Value<double>());
        Assert.Equal(16, json["frame_count"]!.Value<int>());
        Assert.Equal(@"C:\Generated\preview.png", json["image_path"]!.Value<string>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"request\",\"correlation_id\":1,\"action\":\"abort\",\"result\":0}")]
    [InlineData("{\"type\":\"response\",\"correlation_id\":0,\"action\":\"abort\",\"result\":0}")]
    [InlineData("{\"type\":\"response\",\"correlation_id\":1,\"action\":\"abort\",\"result\":2}")]
    public void ValidatePayload_RejectsInvalidLogicalJson(string payload)
    {
        using var directory = new TempDirectory();
        var simulator = CreateSimulator(CreateOptions(directory.Path));

        Assert.False(simulator.ValidatePayload(payload).IsValid);
    }

    [Fact]
    public void ValidatePayload_AppliesActionSpecificRules()
    {
        using var directory = new TempDirectory();
        var simulator = CreateSimulator(CreateOptions(directory.Path));
        const string invalidLive = "{\"type\":\"response\",\"correlation_id\":1,"
                                   + "\"action\":\"live\",\"result\":0,\"hfw\":0.001,"
                                   + "\"frame_count\":8,\"image_path\":\"C:\\\\Images\\\\live.png\"}";

        var validation = simulator.ValidatePayload(invalidLive);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("exactly 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_ConvertsLogicalJsonToXmlWirePayload()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var simulator = CreateSimulator(options);
        const string payload = "{\"type\":\"response\",\"correlation_id\":77,"
                               + "\"action\":\"camera\",\"result\":1,"
                               + "\"current_camera_x\":-3.2e-9,\"current_camera_y\":7.62e-6}";

        await simulator.PublishAsync(payload, CancellationToken.None);

        var bytes = File.ReadAllBytes(Path.Combine(directory.Path, options.ResponseFileName));
        var xml = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("<?xml", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"type\"", xml, StringComparison.Ordinal);
        Assert.True(_codec.TryDeserializeResponse(bytes, out var response));
        Assert.Equal(77, response!.CorrelationId);
        Assert.Equal(EquipmentActionNames.Camera, response.Action);
        Assert.Equal(1, response.Result);
    }

    [Fact]
    public async Task Publish_EquipmentDeleteModeDeletesOnlyMatchingXmlRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        File.WriteAllBytes(requestPath, _codec.SerializeRequest(new EquipmentRequestMessage(
            78,
            EquipmentActionNames.Abort)));
        var simulator = CreateSimulator(options);

        await simulator.PublishAsync(
            "{\"type\":\"response\",\"correlation_id\":78,\"action\":\"abort\",\"result\":0}",
            CancellationToken.None);

        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Publish_MismatchedRequestIsPreservedAndRejected()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var requestBytes = _codec.SerializeRequest(new EquipmentRequestMessage(
            79,
            EquipmentActionNames.Abort));
        File.WriteAllBytes(requestPath, requestBytes);
        var simulator = CreateSimulator(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => simulator.PublishAsync(
            "{\"type\":\"response\",\"correlation_id\":80,\"action\":\"abort\",\"result\":0}",
            CancellationToken.None));

        Assert.Equal(requestBytes, File.ReadAllBytes(requestPath));
    }

    [Fact]
    public async Task ContinuousLiveSimulator_PublishesMatchingXmlResponse()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var request = LiveRequest(81);
        File.WriteAllBytes(
            Path.Combine(directory.Path, options.RequestFileName),
            _codec.SerializeRequest(request));
        var simulator = CreateSimulator(options);
        var snapshot = await simulator.GetActiveRequestAsync(CancellationToken.None);

        var result = await simulator.TryPublishFrameResponseAsync(
            snapshot!,
            @"C:\Generated\frame.png",
            CancellationToken.None);

        Assert.True(result.IsPublished);
        var responseBytes = File.ReadAllBytes(Path.Combine(directory.Path, options.ResponseFileName));
        Assert.True(_codec.TryDeserializeResponse(responseBytes, request, out var response));
        Assert.Equal(1, response!.FrameCount);
        Assert.Equal(1E-3, response.Hfw);
        Assert.Equal(@"C:\Generated\frame.png", response.ImagePath);
    }

    [Fact]
    public async Task ContinuousLiveSimulator_DoesNotOverwriteExistingResponseForSameCorrelation()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var request = LiveRequest(82);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        File.WriteAllBytes(requestPath, _codec.SerializeRequest(request));
        var existing = _codec.SerializeResponse(new EquipmentResponseMessage(
            82,
            EquipmentActionNames.Live,
            0,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = 1,
                ["image_path"] = @"C:\Existing\frame.png"
            }));
        File.WriteAllBytes(responsePath, existing);
        var simulator = CreateSimulator(options);

        var result = await simulator.TryPublishFrameResponseAsync(
            new EquipmentRequestSnapshot(82, EquipmentActionNames.Live, request.Parameters),
            @"C:\Generated\new.png",
            CancellationToken.None);

        Assert.Equal(FrameResponseSimulationStatus.ResponseAlreadyExists, result.Status);
        Assert.Equal(existing, File.ReadAllBytes(responsePath));
    }

    [Fact]
    public async Task ContinuousLiveSimulator_ReportsNonLiveActiveRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        File.WriteAllBytes(
            Path.Combine(directory.Path, options.RequestFileName),
            _codec.SerializeRequest(StageRequest(83)));
        var simulator = CreateSimulator(options);

        var result = await simulator.TryPublishFrameResponseAsync(
            new EquipmentRequestSnapshot(
                84,
                EquipmentActionNames.Live,
                LiveRequest(84).Parameters),
            @"C:\Generated\frame.png",
            CancellationToken.None);

        Assert.Equal(FrameResponseSimulationStatus.ActiveRequestIsNotLive, result.Status);
        Assert.Equal(83, result.ActiveRequest!.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, result.ActiveRequest.Action);
    }

    private JsonEquipmentResponseSimulator CreateSimulator(EquipmentCommunicationOptions options)
    {
        return new JsonEquipmentResponseSimulator(Options.Create(options), _codec);
    }

    private static EquipmentRequestMessage StageRequest(int correlationId)
    {
        return new EquipmentRequestMessage(
            correlationId,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = 0d,
                ["stage_y"] = 0d
            });
    }

    private static EquipmentRequestMessage LiveRequest(int correlationId)
    {
        return new EquipmentRequestMessage(
            correlationId,
            EquipmentActionNames.Live,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = 1,
                ["image_path"] = @"C:\Requested\frame.png"
            });
    }

    private static EquipmentCommunicationOptions CreateOptions(string directory)
    {
        return new EquipmentCommunicationOptions
        {
            ExchangeDirectory = directory,
            RequestFileName = "request.test.xml",
            ResponseFileName = "response.test.xml",
            PollingInterval = TimeSpan.FromMilliseconds(5),
            StableReadDelay = TimeSpan.FromMilliseconds(5),
            ResponseTimeout = TimeSpan.FromSeconds(2)
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DrillFlow-SimulatorTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
            }
        }
    }
}
