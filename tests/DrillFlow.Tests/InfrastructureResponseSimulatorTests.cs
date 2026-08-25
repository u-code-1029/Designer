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
    public async Task CreateDraft_UsesActiveRequestCorrelationAndCommandSpecificTemplate()
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
        Assert.Equal(1E-3d, response.Value<double>("measured_distance"));
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
    public async Task CreateDraft_ProvidesTemplatesForEveryEquipmentAction()
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));
        var drill = new DrillNode
        {
            DrillResultPath = ParameterBinding.Literal(@"C:\results\hole.csv")
        };

        var move = JObject.Parse((await simulator.CreateDraftAsync(
            new MoveNode(), 11, CancellationToken.None)).Payload);
        var measure = JObject.Parse((await simulator.CreateDraftAsync(
            new MeasureNode(), 12, CancellationToken.None)).Payload);
        var drillResponse = JObject.Parse((await simulator.CreateDraftAsync(
            drill, 13, CancellationToken.None)).Payload);
        var abort = JObject.Parse((await simulator.CreateDraftAsync(
            new AbortNode(), 14, CancellationToken.None)).Payload);

        Assert.NotNull(move["position_x"]);
        Assert.NotNull(move["position_y"]);
        Assert.NotNull(measure["measured_distance"]);
        Assert.Equal(@"C:\results\hole.csv", drillResponse.Value<string>("drill_result_path"));
        Assert.Equal(2, abort.Properties().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"index\":1,\"command\":\"measure\"}")]
    [InlineData("{\"index\":1.5,\"command\":\"return\"}")]
    [InlineData("{\"index\":0,\"command\":\"return\"}")]
    [InlineData("{\"index\":-1,\"command\":\"return\"}")]
    [InlineData("{\"index\":999999999999999999999,\"command\":\"return\"}")]
    public void ValidatePayload_RejectsMalformedEquipmentResponse(string payload)
    {
        using var directory = new InfrastructureTestDirectory();
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(CreateOptions(directory.Path)));

        var result = simulator.ValidatePayload(payload);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task PublishAsync_ReplacesResponseAtomicallyAndRemovesTemporaryFile()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        var simulator = new JsonEquipmentResponseSimulator(Options.Create(options));
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        File.WriteAllText(responsePath, "{\"index\":8,\"command\":\"return\"}");
        const string edited = "{\"index\":9,\"command\":\"return\",\"value\":123}";

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
            "{\"index\":55,\"command\":\"return\",\"drill_result_path\":\"result.csv\"}",
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
            "{\"index\":55,\"command\":\"return\"}",
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
        Assert.Equal(1E-3d, response.Properties["measured_distance"]);
        Assert.False(File.Exists(requestPath));
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
