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
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlow.LiveTransportTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new EquipmentCommunicationOptions
            {
                ExchangeDirectory = directory,
                RequestFileName = "request.json",
                ResponseFileName = "response.json",
                EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten,
                ResponseTimeout = TimeSpan.FromSeconds(2),
                PollingInterval = TimeSpan.FromMilliseconds(10),
                StableReadDelay = TimeSpan.FromMilliseconds(5),
            };
            using var transport = new FileEquipmentTransport(
                Options.Create(options),
                NullLogger<FileEquipmentTransport>.Instance);
            using var live = new LiveInteractionSession(
                transport,
                new FixedCorrelationProvider(502),
                NullLogger<LiveInteractionSession>.Instance);
            var requestPath = Path.Combine(directory, options.RequestFileName);
            var responsePath = Path.Combine(directory, options.ResponseFileName);

            var workflowExchange = transport.ExchangeAsync(
                new EquipmentRequestMessage(
                    501,
                    "move",
                    new Dictionary<string, object?>
                    {
                        ["move_mode"] = "relative",
                        ["move_x"] = 1E-3,
                        ["move_y"] = 0d,
                    }),
                CancellationToken.None);
            await WaitForTextContainingAsync(requestPath, "\"index\": 501");

            var liveExchange = live.RequestFrameAsync();
            await Task.Delay(60);
            Assert.Contains("\"index\": 501", File.ReadAllText(requestPath), StringComparison.Ordinal);
            Assert.False(liveExchange.IsCompleted);

            await WriteReplacingAsync(
                responsePath,
                "{\"index\":501,\"command\":\"return\",\"stage_x\":0.01,\"stage_y\":0.02}");
            await workflowExchange;

            await WaitForTextContainingAsync(requestPath, "\"index\": 502");
            Assert.Contains("\"command\": \"frame\"", File.ReadAllText(requestPath), StringComparison.Ordinal);
            await WriteReplacingAsync(
                responsePath,
                "{\"index\":502,\"command\":\"return\",\"stage_x\":0.01,\"stage_y\":0.02,"
                + "\"image_path\":\"C:\\\\camera\\\\frame.png\"}");

            var frame = await liveExchange;
            Assert.Equal(502, frame.Index);
            Assert.Equal(@"C:\camera\frame.png", frame.ImagePath);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task CanceledLiveFrame_CompletesPromptlyAndCleansItsPublishedRequest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlow.LiveTransportTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new EquipmentCommunicationOptions
            {
                ExchangeDirectory = directory,
                RequestFileName = "request.json",
                ResponseFileName = "response.json",
                EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten,
                ResponseTimeout = TimeSpan.FromSeconds(30),
                PollingInterval = TimeSpan.FromMilliseconds(10),
                StableReadDelay = TimeSpan.FromMilliseconds(5),
            };
            using var transport = new FileEquipmentTransport(
                Options.Create(options),
                NullLogger<FileEquipmentTransport>.Instance);
            using var live = new LiveInteractionSession(
                transport,
                new FixedCorrelationProvider(601),
                NullLogger<LiveInteractionSession>.Instance);
            using var cancellation = new CancellationTokenSource();
            var requestPath = Path.Combine(directory, options.RequestFileName);
            var exchange = live.RequestFrameAsync(cancellation.Token);
            await WaitForTextContainingAsync(requestPath, "\"index\": 601");

            cancellation.Cancel();
            var completed = await Task.WhenAny(exchange, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(exchange, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
            await WaitForFileMissingAsync(requestPath);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

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

    private static Task WriteReplacingAsync(string destination, string content)
    {
        var tempPath = destination + ".test.tmp";
        File.WriteAllText(tempPath, content, new UTF8Encoding(false));
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
        private readonly int _index;

        public FixedCorrelationProvider(int index)
        {
            _index = index;
        }

        public Task<int> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_index);
        }
    }
}
