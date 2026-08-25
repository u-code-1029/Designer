using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureFileTransportTests
{
    [Fact]
    public async Task DeleteAfterRead_WaitsForDelayedDeletionBeforeCompletingAndPublishingNextAction()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ResponseTimeout = TimeSpan.FromSeconds(1);
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var firstExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(201, "measure"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":201,\"command\":\"return\"}");

        await Task.Delay(80);
        Assert.False(firstExchange.IsCompleted, "The exchange completed before equipment deleted its request.");

        File.Delete(requestPath);
        Assert.Equal(201, (await firstExchange).Index);

        var secondExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(202, "abort"),
            CancellationToken.None);
        var secondPayload = await WaitForTextAsync(requestPath);
        Assert.Contains("\"index\": 202", secondPayload, StringComparison.Ordinal);
        File.Delete(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":202,\"command\":\"return\"}");

        Assert.Equal(202, (await secondExchange).Index);
    }

    [Fact]
    public async Task DeleteAfterRead_WaitsForStaleFirstRunFileInsteadOfOverwritingIt()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ResponseTimeout = TimeSpan.FromMilliseconds(500);
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        File.WriteAllText(requestPath, "stale request from an earlier app run");

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(203, "move"),
            CancellationToken.None);

        await Task.Delay(60);
        Assert.Equal("stale request from an earlier app run", File.ReadAllText(requestPath));
        File.Delete(requestPath);

        var newPayload = await WaitForTextAsync(requestPath);
        Assert.Contains("\"index\": 203", newPayload, StringComparison.Ordinal);
        File.Delete(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":203,\"command\":\"return\"}");

        Assert.Equal(203, (await exchange).Index);
    }

    [Fact]
    public async Task DeleteAfterRead_TimesOutSafelyWhenStaleFirstRunFileNeverDisappears()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        const string stalePayload = "do not overwrite this uncertain request";
        File.WriteAllText(requestPath, stalePayload);

        var exception = await Assert.ThrowsAsync<EquipmentRequestDeletionTimeoutException>(
            () => transport.ExchangeAsync(
                new EquipmentRequestMessage(204, "measure"),
                CancellationToken.None));

        Assert.Equal(EquipmentRequestDeletionWaitPhase.BeforeInitialPublish, exception.Phase);
        Assert.Equal(stalePayload, File.ReadAllText(requestPath));
    }

    [Fact]
    public async Task DeleteAfterRead_DoesNotRetryUntilTimedOutRequestHasBeenDeleted()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        options.RetryEnabled = true;
        options.MaximumRetryCount = 1;
        options.RetryDelay = TimeSpan.FromMilliseconds(10);
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(
                205,
                "drill",
                new Dictionary<string, object?> { ["thickness"] = 2.4E-3 }),
            CancellationToken.None);
        var firstPayload = await WaitForTextAsync(requestPath);

        // This is past the response timeout. The old implementation had already replaced the
        // pathname here, allowing this delayed equipment delete to remove the retry instead.
        await Task.Delay(140);
        Assert.False(exchange.IsCompleted);
        File.Delete(requestPath);

        var retryPayload = await WaitForTextAsync(requestPath, TimeSpan.FromSeconds(2));
        Assert.Equal(firstPayload, retryPayload);
        File.Delete(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":205,\"command\":\"return\"}");

        Assert.Equal(205, (await exchange).Index);
    }

    [Fact]
    public async Task DeleteAfterRead_MatchingResponseIsNotConsumedWhenRequestDeletionTimesOut()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ResponseTimeout = TimeSpan.FromMilliseconds(120);
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(206, "measure"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":206,\"command\":\"return\"}");

        var exception = await Assert.ThrowsAsync<EquipmentRequestDeletionTimeoutException>(
            () => exchange);

        Assert.Equal(EquipmentRequestDeletionWaitPhase.AfterMatchingResponse, exception.Phase);
        Assert.True(File.Exists(responsePath));
    }

    [Fact]
    public async Task Exchange_UsesScientificNumbersAndDeletesResponseByDefault()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = new EquipmentRequestMessage(
            101,
            "move",
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["move_x"] = 1E-3,
                ["move_y"] = -2.56E-4,
            });

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        var equipment = Task.Run(async () =>
        {
            var requestPath = Path.Combine(directory.Path, options.RequestFileName);
            var requestJson = await WaitForTextAsync(requestPath);
            Assert.Contains("\"move_x\": 1E-3", requestJson, StringComparison.Ordinal);
            Assert.Contains("\"move_y\": -2.56E-4", requestJson, StringComparison.Ordinal);
            File.Delete(requestPath);
            await WriteReplacingAsync(
                Path.Combine(directory.Path, options.ResponseFileName),
                "{\"index\":101,\"command\":\"return\",\"drill_result_path\":\"C:\\\\results\\\\r.csv\"}");
        });

        var response = await exchange;
        await equipment;

        Assert.Equal(@"C:\results\r.csv", response.Properties["drill_result_path"]);
        Assert.False(File.Exists(Path.Combine(directory.Path, options.ResponseFileName)));
    }

    [Fact]
    public async Task Exchange_IgnoresStaleResponseAndRetainsMatchingResponseWhenConfigured()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        File.WriteAllText(responsePath, "{\"index\":7,\"command\":\"return\",\"value\":\"stale\"}");
        using var transport = CreateTransport(options);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(102, "measure"),
            CancellationToken.None);
        var equipment = Task.Run(async () =>
        {
            await WaitForTextAsync(Path.Combine(directory.Path, options.RequestFileName));
            await WriteReplacingAsync(
                responsePath,
                "{\"index\":102,\"command\":\"return\",\"measured_distance\":0.0012}");
        });

        var response = await exchange;
        await equipment;

        Assert.Equal(0.0012d, response.Properties["measured_distance"]);
        Assert.True(File.Exists(responsePath));
        Assert.Contains("\"index\":102", File.ReadAllText(responsePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutRetry_ResendsIdenticalPayloadWithTheSameCorrelationId()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(140);
        options.RetryEnabled = true;
        options.MaximumRetryCount = 1;
        options.RetryDelay = TimeSpan.FromMilliseconds(15);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        using var transport = CreateTransport(options);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(
                103,
                "drill",
                new Dictionary<string, object?> { ["thickness"] = 2.4E-3 }),
            CancellationToken.None);

        var equipment = Task.Run(async () =>
        {
            var requestPath = Path.Combine(directory.Path, options.RequestFileName);
            var firstPayload = await WaitForTextAsync(requestPath);
            File.Delete(requestPath);
            var secondPayload = await WaitForTextAsync(requestPath, TimeSpan.FromSeconds(3));
            Assert.Equal(firstPayload, secondPayload);
            Assert.Contains("\"index\": 103", secondPayload, StringComparison.Ordinal);
            File.Delete(requestPath);
            await WriteReplacingAsync(
                Path.Combine(directory.Path, options.ResponseFileName),
                "{\"index\":103,\"command\":\"return\"}");
        });

        var response = await exchange;
        await equipment;

        Assert.Equal(103, response.Index);
    }

    [Fact]
    public async Task Exchange_ObservesCancellationWhileWaiting()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromSeconds(5);
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExchangeAsync(
                new EquipmentRequestMessage(104, "abort"),
                cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));

        // Cancellation must release both the in-process semaphore and the cross-process lock so
        // an immediate next exchange can publish normally.
        var nextExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(105, "abort"),
            CancellationToken.None);
        await WaitForTextContainingAsync(
            Path.Combine(directory.Path, options.RequestFileName),
            "\"index\": 105");
        await WriteReplacingAsync(
            Path.Combine(directory.Path, options.ResponseFileName),
            "{\"index\":105,\"command\":\"return\"}");
        Assert.Equal(105, (await nextExchange).Index);
    }

    [Fact]
    public async Task ExchangeLock_SerializesSeparateTransportInstancesAndReleasesAfterCompletion()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var firstTransport = CreateTransport(options);
        using var secondTransport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);

        var firstExchange = firstTransport.ExchangeAsync(
            new EquipmentRequestMessage(301, "measure"),
            CancellationToken.None);
        var firstPayload = await WaitForTextAsync(requestPath);

        var secondExchange = secondTransport.ExchangeAsync(
            new EquipmentRequestMessage(302, "abort"),
            CancellationToken.None);
        await Task.Delay(80);

        Assert.Equal(firstPayload, File.ReadAllText(requestPath));
        Assert.False(secondExchange.IsCompleted, "A second transport bypassed the shared sidecar lock.");

        await WriteReplacingAsync(responsePath, "{\"index\":301,\"command\":\"return\"}");
        Assert.Equal(301, (await firstExchange).Index);

        var secondPayload = await WaitForTextContainingAsync(requestPath, "\"index\": 302");
        Assert.Contains("\"command\": \"abort\"", secondPayload, StringComparison.Ordinal);
        await WriteReplacingAsync(responsePath, "{\"index\":302,\"command\":\"return\"}");
        Assert.Equal(302, (await secondExchange).Index);

        Assert.True(File.Exists(lockPath));
        using var releasedLock = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task ExchangeLock_WaitIsCancellableAndDoesNotPublishRequest()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromSeconds(2);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);
        using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExchangeAsync(
                new EquipmentRequestMessage(303, "move"),
                cancellation.Token));

        Assert.False(File.Exists(Path.Combine(directory.Path, options.RequestFileName)));
    }

    [Fact]
    public async Task ExchangeLock_ContentionHasClearBoundedTimeoutAndDoesNotPublishRequest()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);
        using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var transport = CreateTransport(options);

        var exception = await Assert.ThrowsAsync<EquipmentExchangeLockTimeoutException>(
            () => transport.ExchangeAsync(
                new EquipmentRequestMessage(304, "drill"),
                CancellationToken.None));

        Assert.Equal(lockPath, exception.LockFilePath);
        Assert.Equal(options.ResponseTimeout, exception.Timeout);
        Assert.False(File.Exists(Path.Combine(directory.Path, options.RequestFileName)));
    }

    private static EquipmentCommunicationOptions CreateOptions(string directory)
    {
        return new EquipmentCommunicationOptions
        {
            ExchangeDirectory = directory,
            RequestFileName = "equipment.request.json",
            ResponseFileName = "equipment.response.json",
            ResponseTimeout = TimeSpan.FromSeconds(2),
            PollingInterval = TimeSpan.FromMilliseconds(10),
            StableReadDelay = TimeSpan.FromMilliseconds(5),
            RetryDelay = TimeSpan.FromMilliseconds(10),
        };
    }

    private static FileEquipmentTransport CreateTransport(EquipmentCommunicationOptions options)
    {
        return new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance);
    }

    private static async Task<string> WaitForTextAsync(string path, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync();
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Test equipment did not observe '{path}'.");
    }

    private static async Task<string> WaitForTextContainingAsync(
        string path,
        string expected,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
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

        throw new TimeoutException($"Test equipment did not observe '{expected}' in '{path}'.");
    }

    private static Task WriteReplacingAsync(string destination, string content)
    {
        var temp = destination + ".test.tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(destination))
        {
            File.Replace(temp, destination, null);
        }
        else
        {
            File.Move(temp, destination);
        }

        return Task.CompletedTask;
    }
}
