using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Infrastructure.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureLiveInteractionSessionTests
{
    [Fact]
    public async Task SharedFileTransport_ExcludesLiveExchangeWhileWorkflowExchangeIsInFlight()
    {
        using var directory = new LiveTransportTestDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance);
        using var live = CreateLiveSession(
            transport,
            new FixedCorrelationProvider(502),
            options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var workflowExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(
                501,
                "stage",
                new Dictionary<string, object?>
                {
                    ["move_mode"] = "relative",
                    ["stage_x"] = 1E-3,
                    ["stage_y"] = 0d,
                }),
            CancellationToken.None);
        await WaitForTextContainingAsync(requestPath, "<correlation_id>501</correlation_id>");

        var liveExchange = live.RequestFrameAsync(1E-3);
        await Task.Delay(60);
        Assert.Contains("<correlation_id>501</correlation_id>", File.ReadAllText(requestPath));
        Assert.False(liveExchange.IsCompleted);

        await WriteResponseAsync(
            responsePath,
            new EquipmentResponseMessage(
                501,
                "stage",
                0,
                new Dictionary<string, object?>
                {
                    ["current_stage_x"] = 0.01,
                    ["current_stage_y"] = 0.02,
                }));
        await workflowExchange;

        var livePayload = await WaitForTextContainingAsync(
            requestPath,
            "<correlation_id>502</correlation_id>");
        Assert.Contains("<action>live</action>", livePayload);
        Assert.Contains("<hfw>1E-3</hfw>", livePayload);
        Assert.Contains("<frame_count>1</frame_count>", livePayload);
        await WriteResponseAsync(
            responsePath,
            new EquipmentResponseMessage(
                502,
                "live",
                0,
                new Dictionary<string, object?>
                {
                    ["hfw"] = 1E-3,
                    ["frame_count"] = 1,
                    ["image_path"] = @"C:\camera\frame.png",
                }));

        var frame = await liveExchange;
        Assert.Equal(502, frame.Response.CorrelationId);
        Assert.Equal(@"C:\camera\frame.png", frame.Response.ImagePath);
        Assert.False(frame.OwnsResponseImage);
    }

    [Fact]
    public async Task CanceledLiveRequest_CompletesPromptlyAndCleansPublishedXml()
    {
        using var directory = new LiveTransportTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromSeconds(30);
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance);
        using var live = CreateLiveSession(
            transport,
            new FixedCorrelationProvider(601),
            options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var exchange = live.RequestFrameAsync(1E-3, cancellation.Token);
        await WaitForTextContainingAsync(requestPath, "<correlation_id>601</correlation_id>");

        cancellation.Cancel();
        var completed = await Task.WhenAny(exchange, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(exchange, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await WaitForFileMissingAsync(requestPath);
    }

    [Fact]
    public async Task CanceledLiveRequest_IsReclaimedBeforeInteractiveStageMovePublishes()
    {
        using var directory = new LiveTransportTestDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance);
        using var live = CreateLiveSession(
            transport,
            new IncrementingCorrelationProvider(700),
            options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        using var frameCancellation = new CancellationTokenSource();
        var frame = live.RequestFrameAsync(1E-3, frameCancellation.Token);
        await WaitForTextContainingAsync(requestPath, "<correlation_id>701</correlation_id>");

        frameCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frame);
        var move = live.MoveStageAsync("relative", 1E-3, -2E-3);
        var movePayload = await WaitForTextContainingAsync(
            requestPath,
            "<correlation_id>702</correlation_id>");

        Assert.Contains("<action>stage</action>", movePayload);
        Assert.Contains("<stage_x>1E-3</stage_x>", movePayload);
        Assert.DoesNotContain("<correlation_id>701</correlation_id>", movePayload);
        await WriteResponseAsync(
            responsePath,
            new EquipmentResponseMessage(
                702,
                "stage",
                0,
                new Dictionary<string, object?>
                {
                    ["current_stage_x"] = 1E-3,
                    ["current_stage_y"] = -2E-3,
                }));
        var response = await move.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(702, response.CorrelationId);
        Assert.Equal(1E-3, response.CurrentStageX!.Value);
        Assert.Equal(-2E-3, response.CurrentStageY!.Value);
    }

    [Fact]
    public async Task CanceledInteractiveStageMove_IsReclaimedBeforeLiveFramePublishes()
    {
        using var directory = new LiveTransportTestDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance);
        using var live = CreateLiveSession(
            transport,
            new IncrementingCorrelationProvider(800),
            options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        using var moveCancellation = new CancellationTokenSource();
        var move = live.MoveStageAsync(
            "relative",
            1E-3,
            -2E-3,
            moveCancellation.Token);
        await WaitForTextContainingAsync(requestPath, "<correlation_id>801</correlation_id>");

        moveCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        var frame = live.RequestFrameAsync(1E-3);
        var framePayload = await WaitForTextContainingAsync(
            requestPath,
            "<correlation_id>802</correlation_id>");

        Assert.Contains("<action>live</action>", framePayload);
        Assert.DoesNotContain("<correlation_id>801</correlation_id>", framePayload);
        await WriteResponseAsync(
            responsePath,
            new EquipmentResponseMessage(
                802,
                "live",
                0,
                new Dictionary<string, object?>
                {
                    ["hfw"] = 1E-3,
                    ["frame_count"] = 1,
                    ["image_path"] = @"C:\camera\frame.png",
                }));
        var response = await frame.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(802, response.Response.CorrelationId);
        Assert.Equal(@"C:\camera\frame.png", response.Response.ImagePath);
    }

    private static EquipmentCommunicationOptions CreateOptions(string directory) => new()
    {
        ExchangeDirectory = directory,
        RequestFileName = "request.xml",
        ResponseFileName = "response.xml",
        EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten,
        ResponseTimeout = TimeSpan.FromSeconds(2),
        PollingInterval = TimeSpan.FromMilliseconds(10),
        StableReadDelay = TimeSpan.FromMilliseconds(5),
    };

    private static LiveInteractionSession CreateLiveSession(
        IEquipmentFileTransport transport,
        ICorrelationIdProvider correlationIds,
        EquipmentCommunicationOptions options) =>
        new(
            transport,
            correlationIds,
            Options.Create(options),
            NullLogger<LiveInteractionSession>.Instance);

    private static async Task<string> WaitForTextContainingAsync(string path, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains(expected, StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Did not observe '{expected}' in '{path}'.");
    }

    private static Task WriteResponseAsync(
        string destination,
        EquipmentResponseMessage response)
    {
        var codec = new XmlTemplateEquipmentMessageCodec();
        var tempPath = destination + ".test.tmp";
        File.WriteAllBytes(tempPath, codec.SerializeResponse(response));
        if (File.Exists(destination))
        {
            File.Replace(tempPath, destination, null);
        }
        else
        {
            File.Move(tempPath, destination);
        }

        return Task.CompletedTask;
    }

    private static async Task WaitForFileMissingAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.False(File.Exists(path));
    }

    private sealed class FixedCorrelationProvider : ICorrelationIdProvider
    {
        private readonly int _correlationId;

        public FixedCorrelationProvider(int correlationId)
        {
            _correlationId = correlationId;
        }

        public Task<int> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_correlationId);
        }
    }

    private sealed class IncrementingCorrelationProvider : ICorrelationIdProvider
    {
        private int _value;

        public IncrementingCorrelationProvider(int initialValue)
        {
            _value = initialValue;
        }

        public Task<int> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref _value));
        }
    }

    private sealed class LiveTransportTestDirectory : IDisposable
    {
        public LiveTransportTestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DrillFlow.LiveTransportTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
