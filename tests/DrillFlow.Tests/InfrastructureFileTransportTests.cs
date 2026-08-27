using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.Communication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureFileTransportTests
{
    [Fact]
    public async Task Exchange_FromSingleThreadContext_QueuesSerializationAndFileIoOffCaller()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        using var serializationEntered = new ManualResetEventSlim();
        using var allowSerialization = new ManualResetEventSlim();
        var probe = new BlockingSerializationProbe(serializationEntered, allowSerialization);
        var callerThreadId = Thread.CurrentThread.ManagedThreadId;
        var previousContext = SynchronizationContext.Current;
        Task<EquipmentResponseMessage> exchange;
        var callElapsed = Stopwatch.StartNew();

        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            exchange = transport.ExchangeAsync(
                new EquipmentRequestMessage(
                    401,
                    "measure",
                    new Dictionary<string, object?> { ["probe"] = probe }),
                CancellationToken.None);
        }
        finally
        {
            callElapsed.Stop();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        try
        {
            Assert.True(
                callElapsed.Elapsed < TimeSpan.FromMilliseconds(500),
                "ExchangeAsync blocked its caller for " + callElapsed.Elapsed.TotalMilliseconds
                + " ms before returning a Task.");
            Assert.True(
                serializationEntered.Wait(TimeSpan.FromSeconds(3)),
                "The queued serializer did not start.");
            Assert.NotEqual(callerThreadId, probe.GetObservedThreadId());
        }
        finally
        {
            allowSerialization.Set();
        }

        await WaitForTextAsync(Path.Combine(directory.Path, options.RequestFileName));
        await WriteReplacingAsync(
            Path.Combine(directory.Path, options.ResponseFileName),
            "{\"index\":401,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        Assert.Equal(401, (await exchange).Index);
    }

    [Fact]
    public async Task Dispose_DuringActiveExchange_DoesNotBreakItsCompletionOrGateRelease()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(402, "frame"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);
        var queuedExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(403, "frame"),
            CancellationToken.None);
        await Task.Delay(30);

        transport.Dispose();
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":402,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        Assert.Equal(402, (await exchange).Index);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queuedExchange);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.ExchangeAsync(
                new EquipmentRequestMessage(404, "frame"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_DrainsCanceledRequestCleanupBeforeProcessExit()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        // Make the ownership verification observably asynchronous. Without a disposal drain the
        // process boundary returns while this exact request is still present.
        options.StableReadDelay = TimeSpan.FromMilliseconds(750);
        var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);

        try
        {
            var exchange = transport.ExchangeAsync(
                new EquipmentRequestMessage(
                    410,
                    "frame",
                    new Dictionary<string, object?> { ["hfw"] = 5E-3d }),
                cancellation.Token);
            await WaitForTextAsync(requestPath);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);

            var disposeElapsed = Stopwatch.StartNew();
            transport.Dispose();
            disposeElapsed.Stop();

            Assert.False(File.Exists(requestPath));
            Assert.True(
                disposeElapsed.Elapsed < TimeSpan.FromSeconds(3),
                "Transport disposal exceeded the canceled-request cleanup budget: "
                + disposeElapsed.Elapsed.TotalMilliseconds
                + " ms.");
        }
        finally
        {
            transport.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_DrainPreservesMismatchedNewerRequestPayload()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        options.StableReadDelay = TimeSpan.FromMilliseconds(200);
        var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        const string newerPayload =
            "{\"index\":999,\"command\":\"move\",\"move_mode\":\"relative\","
            + "\"move_x\":0,\"move_y\":0}";

        try
        {
            var exchange = transport.ExchangeAsync(
                new EquipmentRequestMessage(411, "frame"),
                cancellation.Token);
            await WaitForTextAsync(requestPath);
            await WriteReplacingAsync(requestPath, newerPayload);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
            transport.Dispose();

            Assert.Equal(newerPayload, File.ReadAllText(requestPath));
        }
        finally
        {
            transport.Dispose();
        }
    }

    [Theory]
    [InlineData(EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)]
    [InlineData(EquipmentRequestFileLifecycle.RetainUntilOverwritten)]
    public async Task CanceledExchange_RemovesOnlyItsPublishedRequestWithoutAbort(
        EquipmentRequestFileLifecycle equipmentLifecycle)
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = equipmentLifecycle;
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(405, "move"),
            cancellation.Token);
        var payload = await WaitForTextAsync(requestPath);
        Assert.Contains("\"index\": 405", payload, StringComparison.Ordinal);
        Assert.Contains("\"command\": \"move\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"command\": \"abort\"", payload, StringComparison.Ordinal);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await WaitForMissingAsync(requestPath);

        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task CanceledExchange_LockedRequestIsDeletedAfterLockIsReleased()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(406, "drill"),
            cancellation.Token);
        await WaitForTextAsync(requestPath);

        using (var equipmentHandle = new FileStream(
                   requestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
            elapsed.Stop();

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(1),
                "Canceled exchange waited for request cleanup for "
                + elapsed.Elapsed.TotalMilliseconds
                + " ms.");
            await Task.Delay(100);
            Assert.True(File.Exists(requestPath));
        }

        await WaitForMissingAsync(requestPath);
        await WaitForExclusiveOpenAsync(lockPath);
    }

    [Fact]
    public async Task CanceledExchange_PermanentDeleteFailureIsNonfatalAndEventuallyReleasesLock()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        var logger = new RecordingLogger<FileEquipmentTransport>();
        using var transport = CreateTransport(options, logger);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(408, "measure"),
            cancellation.Token);
        await WaitForTextAsync(requestPath);

        using (var equipmentHandle = new FileStream(
                   requestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
            await WaitForConditionAsync(
                () => logger.ContainsEntry(
                    entry => entry.Level == LogLevel.Warning
                             && entry.Message.Contains(
                                 "workflow remains stopped",
                                 StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(3));

            Assert.True(File.Exists(requestPath));
            await WaitForExclusiveOpenAsync(lockPath);
        }
    }

    [Fact]
    public async Task CanceledExchange_PreservesRequestWhosePayloadNoLongerMatchesOwnership()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);
        const string replacement =
            "{\"index\":999,\"command\":\"move\",\"move_mode\":\"relative\","
            + "\"move_x\":0,\"move_y\":0}";

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(407, "measure"),
            cancellation.Token);
        await WaitForTextAsync(requestPath);
        await WriteReplacingAsync(requestPath, replacement);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await WaitForExclusiveOpenAsync(lockPath);

        Assert.Equal(replacement, File.ReadAllText(requestPath));
    }

    [Fact]
    public async Task CanceledExchange_StableReadDelayIsIncludedInCleanupBudget()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        options.ResponseTimeout = TimeSpan.FromSeconds(5);
        options.StableReadDelay = TimeSpan.FromSeconds(5);
        options.PollingInterval = TimeSpan.FromMilliseconds(10);
        var logger = new RecordingLogger<FileEquipmentTransport>();
        using var transport = CreateTransport(options, logger);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var lockPath = Path.Combine(
            directory.Path,
            EquipmentCommunicationOptions.ExchangeLockFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(409, "measure"),
            cancellation.Token);
        await WaitForTextAsync(requestPath);

        var cleanupElapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await WaitForConditionAsync(
            () => logger.ContainsEntry(
                entry => entry.Level == LogLevel.Warning
                         && entry.Exception is TimeoutException
                         && entry.Message.Contains(
                             "workflow remains stopped",
                             StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(4));
        await WaitForExclusiveOpenAsync(lockPath);
        cleanupElapsed.Stop();

        Assert.True(
            cleanupElapsed.Elapsed < TimeSpan.FromSeconds(3),
            "StableReadDelay escaped the two-second cleanup budget: "
            + cleanupElapsed.Elapsed.TotalMilliseconds
            + " ms.");
        Assert.True(File.Exists(requestPath));
    }

    [Fact]
    public async Task CanceledExchange_ImmediateNextExchangeWaitsForOwnedCleanupOutsideLockTimeout()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        options.ResponseTimeout = TimeSpan.FromMilliseconds(120);
        options.PollingInterval = TimeSpan.FromMilliseconds(10);
        options.StableReadDelay = TimeSpan.FromMilliseconds(5);
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var first = transport.ExchangeAsync(
            new EquipmentRequestMessage(410, "move"),
            cancellation.Token);
        await WaitForTextAsync(requestPath);

        Task<EquipmentResponseMessage> second;
        using (var equipmentHandle = new FileStream(
                   requestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            second = transport.ExchangeAsync(
                new EquipmentRequestMessage(411, "measure"),
                CancellationToken.None);

            // This exceeds the second exchange's lock timeout. It must still be queued behind
            // same-instance cleanup rather than entering AcquireExchangeLockAsync and faulting.
            await Task.Delay(250);
            Assert.False(second.IsCompleted);
        }

        await WaitForTextContainingAsync(
            requestPath,
            "\"index\": 411",
            TimeSpan.FromSeconds(2));
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":411,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        Assert.Equal(411, (await second).Index);
    }

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
            "{\"index\":201,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

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
            "{\"index\":202,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

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
            "{\"index\":203,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

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
            "{\"index\":205,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

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
            "{\"index\":206,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

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
                "{\"index\":101,\"command\":\"return\",\"stage_x\":0.125,\"stage_y\":-0.25,"
                + "\"image_path\":\"C:\\\\results\\\\r.png\",\"controller_value\":17}");
        });

        var response = await exchange;
        await equipment;

        Assert.Equal(0.125d, response.StageX);
        Assert.Equal(-0.25d, response.StageY);
        Assert.Equal(@"C:\results\r.png", response.ImagePath);
        Assert.Equal(17, response.Properties["controller_value"]);
        Assert.False(File.Exists(Path.Combine(directory.Path, options.ResponseFileName)));
    }

    [Fact]
    public async Task Exchange_DeletesRetainedRequestAfterMatchingResponseByDefault()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(107, "frame"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":107,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        Assert.Equal(107, (await exchange).Index);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Exchange_DefaultCleanup_DeletesRequestBeforeDeletingReadResponse()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        options.PollingInterval = TimeSpan.FromMilliseconds(100);
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(115, "measure"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);

        // Keep the completed response readable while denying delete sharing. This exposes the
        // cleanup boundary: after capturing and validating the response, the app must first remove
        // its completed request, materialize the result, and only then retry response cleanup.
        using (var equipmentResponse = new FileStream(
                   responsePath,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.Read))
        using (var writer = new StreamWriter(
                   equipmentResponse,
                   new System.Text.UTF8Encoding(false),
                   1024,
                   true))
        {
            await writer.WriteAsync(
                "{\"index\":115,\"command\":\"return\",\"stage_x\":0.25,\"stage_y\":-0.5}");
            await writer.FlushAsync();
            equipmentResponse.Flush(true);

            await WaitForMissingAsync(requestPath);

            Assert.True(File.Exists(responsePath));
            Assert.False(exchange.IsCompleted);
        }

        var response = await exchange;

        Assert.Equal(115, response.Index);
        Assert.Equal(0.25d, response.StageX);
        Assert.Equal(-0.5d, response.StageY);
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public async Task Exchange_RequestCleanupFailure_DoesNotFailResponseOrNextExchange()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var firstExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(108, "frame"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);

        // Deny delete sharing to model an equipment process that still has the completed request
        // open. The matching response must remain successful even though app cleanup cannot run.
        using (var equipmentHandle = new FileStream(
                   requestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            await WriteReplacingAsync(
                responsePath,
                "{\"index\":108,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

            Assert.Equal(108, (await firstExchange).Index);
            Assert.True(File.Exists(requestPath));
            Assert.False(File.Exists(responsePath));
        }

        var secondExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(109, "measure"),
            CancellationToken.None);
        await WaitForTextContainingAsync(requestPath, "\"index\": 109");
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":109,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        Assert.Equal(109, (await secondExchange).Index);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Exchange_ResponseCleanupFailure_LogsWarningAndDoesNotFailNextExchange()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        options.PollingInterval = TimeSpan.FromMilliseconds(250);
        var logger = new RecordingLogger<FileEquipmentTransport>();
        using var transport = CreateTransport(options, logger);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var firstExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(110, "measure"),
            CancellationToken.None);
        await WaitForTextAsync(requestPath);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":110,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        // The response remains readable, but the app cannot delete it while this simulated
        // equipment handle denies delete sharing.
        using (var equipmentHandle = new FileStream(
                   responsePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            Assert.Equal(110, (await firstExchange).Index);
            Assert.True(File.Exists(responsePath));
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Warning
                         && entry.Exception is IOException
                         && entry.Message.Contains("response file", StringComparison.OrdinalIgnoreCase)
                         && entry.Message.Contains(
                             "subsequent exchanges will still be attempted",
                             StringComparison.OrdinalIgnoreCase));
        }

        var secondExchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(111, "measure"),
            CancellationToken.None);
        await WaitForTextContainingAsync(requestPath, "\"index\": 111");
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":111,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

        Assert.Equal(111, (await secondExchange).Index);
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public async Task Exchange_RepeatedFrameCleanupFailures_AreRateLimitedAndReportSuppressedCount()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        options.PollingInterval = TimeSpan.FromMilliseconds(250);
        var logger = new RecordingLogger<FileEquipmentTransport>();
        var now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        using var transport = CreateTransport(options, logger, () => now);
        var requestPath = Path.Combine(directory.Path, options.RequestFileName);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        async Task CompleteFrameWithLockedResponseAsync(int index)
        {
            var exchange = transport.ExchangeAsync(
                new EquipmentRequestMessage(index, "frame"),
                CancellationToken.None);
            await WaitForTextContainingAsync(requestPath, "\"index\": " + index);
            await WriteReplacingAsync(
                responsePath,
                "{\"index\":" + index + ",\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");

            using var equipmentHandle = new FileStream(
                responsePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            Assert.Equal(index, (await exchange).Index);
        }

        var firstFrameElapsed = Stopwatch.StartNew();
        await CompleteFrameWithLockedResponseAsync(112);
        firstFrameElapsed.Stop();
        Assert.True(
            firstFrameElapsed.Elapsed < TimeSpan.FromMilliseconds(1500),
            "A locked live response delayed the completed frame for "
            + firstFrameElapsed.Elapsed.TotalMilliseconds
            + " ms instead of returning after one cleanup attempt.");

        await CompleteFrameWithLockedResponseAsync(113);

        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Single(warnings);
        Assert.Null(warnings[0].Exception);

        now = now.AddMinutes(1).AddSeconds(1);
        await CompleteFrameWithLockedResponseAsync(114);

        warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, warning => Assert.Null(warning.Exception));
        Assert.Contains("1 repeated cleanup warning", warnings[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "subsequent exchanges will still be attempted",
            warnings[1].Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exchange_IgnoresStaleResponseAndRetainsMatchingResponseWhenConfigured()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        options.ApplicationRequestLifecycle = ApplicationRequestFileLifecycle.RetainUntilOverwritten;
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
                "{\"index\":102,\"command\":\"return\",\"stage_x\":0.0012,\"stage_y\":-0.0034}");
        });

        var response = await exchange;
        await equipment;

        Assert.Equal(0.0012d, response.StageX);
        Assert.Equal(-0.0034d, response.StageY);
        Assert.Null(response.ImagePath);
        Assert.True(File.Exists(responsePath));
        Assert.Contains("\"index\":102", File.ReadAllText(responsePath), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directory.Path, options.RequestFileName)));
    }

    [Fact]
    public async Task Exchange_IgnoresMatchingEnvelopeUntilRequiredResponseFieldsAreValid()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var responsePath = Path.Combine(directory.Path, options.ResponseFileName);

        var exchange = transport.ExchangeAsync(
            new EquipmentRequestMessage(106, "measure"),
            CancellationToken.None);
        await WaitForTextAsync(Path.Combine(directory.Path, options.RequestFileName));

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":0}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":\"0\",\"stage_y\":0}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":0.4,\"stage_y\":0.5,"
            + "\"image_path\":\"\"}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":0.4,\"stage_y\":0.5,"
            + "\"image_path\":\"result.png\"}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":999999999999999999999999999999999999999,\"command\":\"return\","
            + "\"stage_x\":0.4,\"stage_y\":0.5}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":0.4,\"STAGE_X\":0.6,"
            + "\"stage_y\":0.5}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"Index\":106,\"command\":\"return\",\"stage_x\":0.4,"
            + "\"stage_y\":0.5}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"Command\":\"return\","
            + "\"stage_x\":0.4,\"stage_y\":0.5}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":0.4,\"stage_y\":0.5,"
            + "\"iteration_path\":[99]}");
        await Task.Delay(60);
        Assert.False(exchange.IsCompleted);

        const string hugeInteger = "1234567890123456789012345678901234567890";
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":106,\"command\":\"return\",\"stage_x\":0.4,\"stage_y\":0.5,"
            + "\"image_path\":\"C:\\\\results\\\\result.png\",\"huge_integer\":"
            + hugeInteger
            + "}");
        var response = await exchange;

        Assert.Equal(0.4d, response.StageX);
        Assert.Equal(0.5d, response.StageY);
        Assert.Equal(@"C:\results\result.png", response.ImagePath);
        Assert.Equal(hugeInteger, response.Properties["huge_integer"]);
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
                "{\"index\":103,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");
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
            "{\"index\":105,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");
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

        await WriteReplacingAsync(
            responsePath,
            "{\"index\":301,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");
        Assert.Equal(301, (await firstExchange).Index);

        var secondPayload = await WaitForTextContainingAsync(requestPath, "\"index\": 302");
        Assert.Contains("\"command\": \"abort\"", secondPayload, StringComparison.Ordinal);
        await WriteReplacingAsync(
            responsePath,
            "{\"index\":302,\"command\":\"return\",\"stage_x\":0,\"stage_y\":0}");
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

    private static FileEquipmentTransport CreateTransport(
        EquipmentCommunicationOptions options,
        ILogger<FileEquipmentTransport>? logger = null,
        Func<DateTime>? utcNow = null)
    {
        var effectiveLogger = logger ?? NullLogger<FileEquipmentTransport>.Instance;
        return utcNow is null
            ? new FileEquipmentTransport(Options.Create(options), effectiveLogger)
            : new FileEquipmentTransport(Options.Create(options), effectiveLogger, utcNow);
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

    private static async Task WaitForMissingAsync(
        string path,
        TimeSpan? timeout = null)
    {
        await WaitForConditionAsync(
            () => !File.Exists(path),
            timeout ?? TimeSpan.FromSeconds(3));
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected condition was not reached before the test timeout.");
    }

    private static async Task WaitForExclusiveOpenAsync(
        string path,
        TimeSpan? timeout = null)
    {
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            },
            timeout ?? TimeSpan.FromSeconds(3));
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly object _sync = new object();

        public IList<LogEntry> Entries { get; } = new List<LogEntry>();

        public bool ContainsEntry(Func<LogEntry, bool> predicate)
        {
            lock (_sync)
            {
                return Entries.Any(predicate);
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }

        private sealed class EmptyScope : IDisposable
        {
            public static EmptyScope Instance { get; } = new EmptyScope();

            public void Dispose()
            {
            }
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            throw new InvalidOperationException("The transport attempted to post back to the UI context.");
        }
    }

    private sealed class BlockingSerializationProbe
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;
        private int _observedThreadId;

        public BlockingSerializationProbe(
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public int Value
        {
            get
            {
                _observedThreadId = Thread.CurrentThread.ManagedThreadId;
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(3));
                return 42;
            }
        }

        public int GetObservedThreadId() => Volatile.Read(ref _observedThreadId);
    }

    private sealed class LogEntry
    {
        public LogEntry(LogLevel level, string message, Exception? exception)
        {
            Level = level;
            Message = message;
            Exception = exception;
        }

        public LogLevel Level { get; }

        public string Message { get; }

        public Exception? Exception { get; }
    }
}
