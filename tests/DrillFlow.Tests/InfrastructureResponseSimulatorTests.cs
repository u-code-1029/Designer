using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Infrastructure.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureResponseSimulatorTests
{
    [Fact]
    public async Task CreateDraft_UsesActiveRequestCorrelationAndCommonStageTemplate()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        File.WriteAllText(
            Path.Combine(directory.Path, options.RequestFileName),
            "{\"index\":42,\"command\":\"measure\",\"thickness\":1E-3}");
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        var draft = await simulator.CreateDraftAsync(
            new MeasureNode { Key = "measure_1" },
            7,
            CancellationToken.None);
        var response = JObject.Parse(draft.Payload);

        Assert.Equal("JSON", simulator.PayloadFormat);
        Assert.Equal(42, draft.ActiveRequest!.Index);
        Assert.Equal("measure", draft.ActiveRequest.Command);
        Assert.Equal(42, response.Value<int>("index"));
        Assert.Equal("return", response.Value<string>("command"));
        Assert.Equal(0d, response.Value<double>("stage_x"));
        Assert.Equal(0d, response.Value<double>("stage_y"));
        Assert.Null(response["image_path"]);
        Assert.Null(response["measured_distance"]);
        Assert.Equal(Path.Combine(directory.Path, options.ResponseFileName), draft.ResponsePath);
    }

    [Fact]
    public async Task CreateDraft_UsesCurrentExchangeDirectoryAndResponseFileName()
    {
        using var initialDirectory = new InfrastructureTestDirectory();
        using var currentDirectory = new InfrastructureTestDirectory();
        var options = CreateOptions(initialDirectory.Path);
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        // The settings page updates the live options instance. A newly opened dialog must use
        // the values that are current when its draft is created, not the startup values.
        options.ExchangeDirectory = currentDirectory.Path;
        options.ResponseFileName = "current.response.json";

        var draft = await simulator.CreateDraftAsync(
            new MeasureNode(),
            7,
            CancellationToken.None);

        Assert.Equal(
            Path.Combine(currentDirectory.Path, "current.response.json"),
            draft.ResponsePath);
    }

    [Fact]
    public async Task CreateDraft_ProvidesSameCommonFieldsForEveryEquipmentAction()
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));
        var move = JObject.Parse((await simulator.CreateDraftAsync(
            new MoveNode(), 11, CancellationToken.None)).Payload);
        var measure = JObject.Parse((await simulator.CreateDraftAsync(
            new MeasureNode(), 12, CancellationToken.None)).Payload);
        var drillResponse = JObject.Parse((await simulator.CreateDraftAsync(
            new DrillNode(), 13, CancellationToken.None)).Payload);
        var abort = JObject.Parse((await simulator.CreateDraftAsync(
            new AbortNode(), 14, CancellationToken.None)).Payload);

        foreach (var response in new[] { move, measure, drillResponse, abort })
        {
            Assert.Equal(0d, response.Value<double>("stage_x"));
            Assert.Equal(0d, response.Value<double>("stage_y"));
            Assert.Equal(4, response.Properties().Count());
        }
    }

    [Fact]
    public async Task CreateDraft_IncludesGeneratedImagePathWhenProvided()
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));
        var generatedImagePath = Path.Combine(directory.Path, "generated.png");

        var draft = await simulator.CreateDraftAsync(
            new MeasureNode(),
            15,
            CancellationToken.None,
            generatedImagePath);
        var response = JObject.Parse(draft.Payload);

        Assert.Equal(generatedImagePath, response.Value<string>("image_path"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"index\":1,\"command\":\"measure\"}")]
    [InlineData("{\"index\":1.5,\"command\":\"return\"}")]
    [InlineData("{\"index\":0,\"command\":\"return\"}")]
    [InlineData("{\"index\":-1,\"command\":\"return\"}")]
    [InlineData("{\"index\":999999999999999999999,\"command\":\"return\"}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_y\":0}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":null}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":\"0\",\"stage_y\":0}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":NaN,\"stage_y\":0}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":Infinity}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"image_path\":\"  \"}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"image_path\":42}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"image_path\":\"result.png\"}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"image_path\":\"C:result.png\"}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"image_path\":\"\\\\\\\\server\\\\share\"}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"STAGE_X\":1,\"stage_y\":0}")]
    [InlineData("{\"index\":1,\"Index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"Command\":\"return\",\"stage_x\":0,\"stage_y\":0}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"iteration_path\":[]}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,\"value\":1,\"VALUE\":2}")]
    [InlineData("{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_x\":1,\"stage_y\":0}")]
    public void ValidatePayload_RejectsMalformedEquipmentResponse(string payload)
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));

        var result = simulator.ValidatePayload(payload);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidatePayload_AcceptsFiniteCoordinatesOptionalImageAndExtensions()
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));

        var result = simulator.ValidatePayload(
            "{\"index\":1,\"command\":\"return\",\"stage_x\":-0.125,\"stage_y\":2.5E-3,"
            + "\"image_path\":\"C:\\\\images\\\\result.png\",\"controller_value\":17}");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidatePayload_AcceptsAbsoluteUncImagePath()
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));

        var result = simulator.ValidatePayload(
            "{\"index\":1,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,"
            + "\"image_path\":\"\\\\\\\\server\\\\share\\\\result.png\"}");

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task CreateDraft_RejectsRelativeGeneratedImagePath()
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));

        await Assert.ThrowsAsync<ArgumentException>(() => simulator.CreateDraftAsync(
            new MeasureNode(),
            16,
            CancellationToken.None,
            "result.png"));
    }

    [Fact]
    public async Task PublishAsync_ReplacesResponseAtomicallyAndRemovesTemporaryFile()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        File.WriteAllText(
            responsePath,
            "{\"index\":8,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");
        const string edited =
            "{\"index\":9,\"command\":\"return\",\"stage_x\":0.1,\"stage_y\":-0.2,\"value\":123}";

        await simulator.PublishAsync(edited, CancellationToken.None);

        Assert.Equal(edited, File.ReadAllText(responsePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task PublishAsync_DeleteAfterReadMode_ConsumesMatchingRequestBeforeResponse()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        File.WriteAllText(requestPath, "{\"index\":55,\"command\":\"drill\"}");
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        await simulator.PublishAsync(
            "{\"index\":55,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0,"
            + "\"image_path\":\"C:\\\\results\\\\result.png\"}",
            CancellationToken.None);

        Assert.False(File.Exists(requestPath));
        Assert.True(File.Exists(Path.Combine(directory.Path, options.ResponseFileName)));
    }

    [Fact]
    public async Task PublishAsync_DeleteAfterReadMode_NeverDeletesDifferentActiveRequest()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        File.WriteAllText(requestPath, "{\"index\":56,\"command\":\"move\"}");
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => simulator.PublishAsync(
            "{\"index\":55,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}",
            CancellationToken.None));

        Assert.True(File.Exists(requestPath));
        Assert.False(File.Exists(Path.Combine(directory.Path, options.ResponseFileName)));
    }

    [Fact]
    public async Task PublishAsync_CompletesRealDeleteAfterReadTransportExchange()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.DeleteAfterRead;
        options.ResponseTimeout = TimeSpan.FromSeconds(2);
        options.PollingInterval = TimeSpan.FromMilliseconds(5);
        options.StableReadDelay = TimeSpan.FromMilliseconds(5);
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance);
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));
        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(77, "measure"),
            CancellationToken.None);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!File.Exists(requestPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(File.Exists(requestPath));
        var draft = await simulator.CreateDraftAsync(new MeasureNode(), null, CancellationToken.None);
        await simulator.PublishAsync(draft.Payload, CancellationToken.None);
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(77, response.Index);
        Assert.Equal(0d, response.Properties["stage_x"]);
        Assert.Equal(0d, response.Properties["stage_y"]);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task TryPublishFrameResponse_PublishesForTheObservedFrameCorrelation()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        var imagePath = Path.Combine(directory.Path, "frame.png");
        File.WriteAllText(requestPath, "{\"index\":301,\"command\":\"frame\"}");
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));
        var observed = await simulator.GetActiveRequestAsync(CancellationToken.None);

        var result = await simulator.TryPublishFrameResponseAsync(
            observed!,
            imagePath,
            CancellationToken.None);

        Assert.Equal(FrameResponseSimulationStatus.Published, result.Status);
        Assert.True(result.IsPublished);
        var response = JObject.Parse(File.ReadAllText(responsePath));
        Assert.Equal(301, response.Value<int>("index"));
        Assert.Equal("return", response.Value<string>("command"));
        Assert.Equal(imagePath, response.Value<string>("image_path"));
        Assert.Equal(0d, response.Value<double>("stage_x"));
        Assert.Equal(0d, response.Value<double>("stage_y"));
    }

    [Fact]
    public async Task TryPublishFrameResponse_DoesNotOverwriteRealResponseForSameCorrelation()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        const string controllerPayload =
            "{\"index\":302,\"command\":\"return\",\"stage_x\":0.2,\"stage_y\":0.3," +
            "\"image_path\":\"C:\\\\controller\\\\frame.png\"}";
        File.WriteAllText(requestPath, "{\"index\":302,\"command\":\"frame\"}");
        File.WriteAllText(responsePath, controllerPayload);
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        var result = await simulator.TryPublishFrameResponseAsync(
            new EquipmentRequestSnapshot(302, "frame"),
            Path.Combine(directory.Path, "simulated.png"),
            CancellationToken.None);

        Assert.Equal(FrameResponseSimulationStatus.ResponseAlreadyExists, result.Status);
        Assert.Equal(controllerPayload, File.ReadAllText(responsePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task TryPublishFrameResponse_DoesNotPublishAfterActiveRequestChanges()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        File.WriteAllText(requestPath, "{\"index\":304,\"command\":\"frame\"}");
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        var result = await simulator.TryPublishFrameResponseAsync(
            new EquipmentRequestSnapshot(303, "frame"),
            Path.Combine(directory.Path, "simulated.png"),
            CancellationToken.None);

        Assert.Equal(FrameResponseSimulationStatus.ActiveRequestChanged, result.Status);
        Assert.Equal(304, result.ActiveRequest!.Index);
        Assert.False(File.Exists(Path.Combine(directory.Path, options.ResponseFileName)));
    }

    [Fact]
    public async Task TryPublishFrameResponse_ReplacesOnlyRetainedOlderCorrelationResponse()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        File.WriteAllText(requestPath, "{\"index\":305,\"command\":\"frame\"}");
        File.WriteAllText(
            responsePath,
            "{\"index\":299,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));

        var result = await simulator.TryPublishFrameResponseAsync(
            new EquipmentRequestSnapshot(305, "frame"),
            Path.Combine(directory.Path, "simulated.png"),
            CancellationToken.None);

        Assert.Equal(FrameResponseSimulationStatus.Published, result.Status);
        Assert.Equal(305, JObject.Parse(File.ReadAllText(responsePath)).Value<int>("index"));
    }

    private static EquipmentCommunicationOptions CreateOptions(string directory)
    {
        return new EquipmentCommunicationOptions
        {
            ExchangeDirectory = directory,
            RequestFileName = "request.test.json",
            ResponseFileName = "response.test.json"
        };
    }
}
