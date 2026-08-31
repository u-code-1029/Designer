using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.Communication;
using DrillFlow.Infrastructure.Communication.FileExchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureFileTransportTests
{
    private readonly XmlTemplateEquipmentMessageCodec _codec = new();

    [Fact]
    public async Task Exchange_PublishesTemplateXmlAndReturnsMatchingResponse()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = StageRequest(101, 1E-3, -2.56E-4);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        var requestBytes = await WaitForRequestAsync(options, request);
        var requestXml = Encoding.UTF8.GetString(requestBytes);
        Assert.Contains("<type>request</type>", requestXml, StringComparison.Ordinal);
        Assert.Contains("<correlation_id>101</correlation_id>", requestXml, StringComparison.Ordinal);
        Assert.Contains("<stage_x>1E-03</stage_x>", requestXml, StringComparison.Ordinal);
        Assert.Contains("<stage_y>-2.56E-04</stage_y>", requestXml, StringComparison.Ordinal);

        PublishResponse(options, StageResponse(101, 0.125, -0.25));
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(101, response.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, response.Action);
        Assert.Equal(0.125, response.CurrentStageX);
        Assert.Equal(-0.25, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_ReportsPublishedRequestAndMatchedResponseToTraceSink()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var traceSink = new RecordingTraceSink();
        using var transport = CreateTransport(options, traceSink: traceSink);
        var request = StageRequest(133, 1E-3, -2E-3);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        var published = await traceSink.RequestPublished.Task.WithTimeoutAsync(
            TimeSpan.FromSeconds(3));

        Assert.Equal(RequestPath(options), published.FilePath);
        Assert.Same(request, published.Request);
        Assert.Equal(1, published.Attempt);

        PublishResponse(options, StageResponse(133, 0.25, -0.5));
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        var matched = await traceSink.ResponseMatched.Task.WithTimeoutAsync(
            TimeSpan.FromSeconds(3));

        Assert.Equal(ResponsePath(options), matched.FilePath);
        Assert.Same(response, matched.Response);
        Assert.Equal(133, matched.Response.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, matched.Response.Action);
        Assert.Equal(new[] { "request", "response" }, traceSink.NotificationOrder);
    }

    [Fact]
    public async Task Exchange_TraceSinkFailuresDoNotInterruptPhysicalExchange()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var traceSink = new ThrowingTraceSink();
        using var transport = CreateTransport(options, traceSink: traceSink);
        var request = StageRequest(134);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, StageResponse(134, 0.125, -0.25));

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(134, response.CorrelationId);
        Assert.Equal(1, traceSink.RequestPublishedCount);
        Assert.Equal(1, traceSink.ResponseMatchedCount);
        Assert.Equal(0, traceSink.ExchangeStoppedCount);
    }

    [Fact]
    public async Task Exchange_DetectsWhitespaceVariedStageResponseWithPaddedScientificExponents()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = StageRequest(121, 1E-6, -2.56E-3);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishRawResponse(
            options,
            " \t\r\n<?xml\tversion = \"1.0\"\r\n encoding = \"utf-8\"   ?>"
            + "<response \t><type > \tresponse\r\n</type >"
            + "<correlation_id\r\n> 121 </correlation_id >"
            + "<action >\r\nstage\t</action ><result > 0 </result >"
            + "<current_stage_x > \t-3.2E-06\r\n</current_stage_x >"
            + "<current_stage_y\t> 4.12E-04 </current_stage_y >"
            + "</response   >\r\n ");

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(121, response.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, response.Action);
        Assert.Equal(0, response.Result);
        Assert.Equal(-3.2E-6, response.CurrentStageX);
        Assert.Equal(4.12E-4, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_DetectsResponseCopiedIntoConfiguredPathAfterRequestPublication()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = StageRequest(122);
        var sourcePath = Path.Combine(directory.Path, "copied-stage-response.xml");
        File.WriteAllBytes(sourcePath, _codec.SerializeResponse(StageResponse(122, 0.25, -0.5)));

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        File.Copy(sourcePath, ResponsePath(options), overwrite: true);

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(122, response.CorrelationId);
        Assert.Equal(0.25, response.CurrentStageX);
        Assert.Equal(-0.5, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_DetectsUtf8BomResponsePublishedAfterRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = StageRequest(129);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        var responseXml = Encoding.UTF8.GetString(
            _codec.SerializeResponse(StageResponse(129, 0.125, -0.25)));
        PublishRawResponse(options, "\uFEFF" + responseXml);

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(129, response.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, response.Action);
        Assert.Equal(0.125, response.CurrentStageX);
        Assert.Equal(-0.25, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_WaitsForResponseWriterToCloseBeforeReadingPublishedPayload()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = StageRequest(130);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        var responseBytes = _codec.SerializeResponse(StageResponse(130, 0.125, -0.25));
        using (var responseWriter = new FileStream(
                   ResponsePath(options),
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            await responseWriter.WriteAsync(responseBytes, 0, responseBytes.Length);
            await responseWriter.FlushAsync();

            await Task.Delay(
                options.StableReadDelay
                + options.PollingInterval
                + options.StableReadDelay);
            Assert.False(exchange.IsCompleted);
        }

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(130, response.CorrelationId);
        Assert.Equal(0.125, response.CurrentStageX);
        Assert.Equal(-0.25, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_RetainModeCapturesOpenStaleResponseBeforePublishingRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var request = StageRequest(131);
        var staleResponse = _codec.SerializeResponse(StageResponse(131, 1, 2));

        var responseWriter = new FileStream(
            ResponsePath(options),
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            await responseWriter.WriteAsync(staleResponse, 0, staleResponse.Length);
            await responseWriter.FlushAsync();

            var exchange = transport.ExchangeAsync(request, CancellationToken.None);
            await Task.Delay(
                options.StableReadDelay
                + options.PollingInterval
                + options.StableReadDelay);
            Assert.False(File.Exists(RequestPath(options)));

            responseWriter.Dispose();
            await WaitForRequestAsync(options, request);
            await Task.Delay(options.StableReadDelay + options.PollingInterval);
            Assert.False(exchange.IsCompleted);

            PublishResponse(options, StageResponse(131, 3, 4));
            var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(3, response.CurrentStageX);
            Assert.Equal(4, response.CurrentStageY);
        }
        finally
        {
            responseWriter.Dispose();
        }
    }

    [Fact]
    public async Task Exchange_RetainModeDoesNotPublishWhenOpenBaselineNeverBecomesReadable()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(120);
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var request = StageRequest(132);
        var staleResponse = _codec.SerializeResponse(StageResponse(132, 1, 2));

        using (var responseWriter = new FileStream(
                   ResponsePath(options),
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            await responseWriter.WriteAsync(staleResponse, 0, staleResponse.Length);
            await responseWriter.FlushAsync();

            var exception = await Assert.ThrowsAsync<TimeoutException>(
                () => transport.ExchangeAsync(request, CancellationToken.None));

            Assert.Contains("No request was published", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(RequestPath(options)));
        }
    }

    [Fact]
    public async Task Exchange_WaitsConfiguredDelayBeforeInitialRequestPublication()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.RequestPublishDelay = TimeSpan.FromMilliseconds(250);
        using var transport = CreateTransport(options);
        var request = StageRequest(116);
        var elapsed = Stopwatch.StartNew();

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await Task.Delay(75);
        Assert.False(File.Exists(RequestPath(options)));

        await WaitForRequestAsync(options, request);
        elapsed.Stop();
        Assert.True(
            elapsed.Elapsed >= TimeSpan.FromMilliseconds(200),
            $"Request was published after only {elapsed.Elapsed.TotalMilliseconds:F0} ms.");

        PublishResponse(options, StageResponse(116, 0, 0));
        await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Exchange_CanceledDuringInitialDelayLeavesNoRequestOrTemporaryFile()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.RequestPublishDelay = TimeSpan.FromSeconds(2);
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var request = StageRequest(117);

        var exchange = transport.ExchangeAsync(request, cancellation.Token);
        var lockPath = Path.Combine(
            options.ExchangeDirectory,
            EquipmentCommunicationOptions.ExchangeLockFileName);
        await WaitForFileExistsAsync(lockPath);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3)));
        Assert.False(File.Exists(RequestPath(options)));
        Assert.Empty(Directory.GetFiles(
            options.ExchangeDirectory,
            options.RequestFileName + ".*.tmp"));
    }

    [Fact]
    public async Task Exchange_InitialDelayDoesNotConsumeResponseTimeout()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.RequestPublishDelay = TimeSpan.FromMilliseconds(250);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(180);
        using var transport = CreateTransport(options);
        var request = StageRequest(120);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, StageResponse(120, 0, 0));

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(120, response.CorrelationId);
    }

    [Fact]
    public async Task ExchangeGate_AppliesQuietDelayBetweenCompletedAndNextRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.RequestPublishDelay = TimeSpan.FromMilliseconds(250);
        using var transport = CreateTransport(options);
        var firstRequest = StageRequest(118);
        var secondRequest = StageRequest(119);

        var first = transport.ExchangeAsync(firstRequest, CancellationToken.None);
        await WaitForRequestAsync(options, firstRequest);
        PublishResponse(options, StageResponse(118, 0, 0));
        await first.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(RequestPath(options)));

        var elapsed = Stopwatch.StartNew();
        var second = transport.ExchangeAsync(secondRequest, CancellationToken.None);
        await Task.Delay(75);
        Assert.False(File.Exists(RequestPath(options)));

        await WaitForRequestAsync(options, secondRequest);
        elapsed.Stop();
        Assert.True(
            elapsed.Elapsed >= TimeSpan.FromMilliseconds(200),
            $"Next request was published after only {elapsed.Elapsed.TotalMilliseconds:F0} ms.");

        PublishResponse(options, StageResponse(119, 0, 0));
        await second.WithTimeoutAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Exchange_RequiresBothMatchingCorrelationAndAction()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = StageRequest(102);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, StageResponse(999, 0, 0));
        await Task.Delay(80);
        Assert.False(exchange.IsCompleted);

        PublishResponse(options, CameraResponse(102));
        await Task.Delay(80);
        Assert.False(exchange.IsCompleted);

        PublishResponse(options, StageResponse(102, 1E-3, 2E-3));
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(102, response.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, response.Action);
    }

    [Fact]
    public async Task Exchange_ReturnsResultOneWithoutTransportFault()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = new EquipmentRequestMessage(103, EquipmentActionNames.Abort);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, new EquipmentResponseMessage(
            103,
            EquipmentActionNames.Abort,
            1));

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, response.Result);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task Exchange_DefaultLifecycleDeletesCompletedRequestAndResponse()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = new EquipmentRequestMessage(104, EquipmentActionNames.Abort);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, new EquipmentResponseMessage(104, EquipmentActionNames.Abort, 0));
        await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.False(File.Exists(RequestPath(options)));
        Assert.False(File.Exists(ResponsePath(options)));
    }

    [Fact]
    public async Task Exchange_RetainLifecycleLeavesBothFilesForOverwrite()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ApplicationRequestLifecycle = ApplicationRequestFileLifecycle.RetainUntilOverwritten;
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var request = StageRequest(105);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, StageResponse(105, 0, 0));
        await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.True(File.Exists(RequestPath(options)));
        Assert.True(File.Exists(ResponsePath(options)));
        Assert.True(_codec.TryDeserializeRequest(File.ReadAllBytes(RequestPath(options)), out var retained));
        Assert.Equal(105, retained!.CorrelationId);
    }

    [Fact]
    public async Task Exchange_DoesNotAcceptRetainedResponseForDifferentCorrelation()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        var stale = StageResponse(106, 1, 1);
        PublishResponse(options, stale);
        using var transport = CreateTransport(options);
        var request = StageRequest(123);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        await Task.Delay(100);
        Assert.False(exchange.IsCompleted);

        PublishResponse(options, StageResponse(123, 2, 3));
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, response.CurrentStageX);
        Assert.Equal(3, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_RetainModeDoesNotAcceptUnchangedMatchingResponse()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(180);
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        var responseBytes = _codec.SerializeResponse(StageResponse(125, 4, 5));
        File.WriteAllBytes(ResponsePath(options), responseBytes);
        using var transport = CreateTransport(options);
        var request = StageRequest(125);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);

        await Assert.ThrowsAsync<EquipmentResponseTimeoutException>(() => exchange);
        Assert.Equal(responseBytes, File.ReadAllBytes(ResponsePath(options)));
    }

    [Fact]
    public async Task Exchange_DefaultLifecycleRemovesStaleResponseAndDetectsIdenticalCopy()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var responseBytes = _codec.SerializeResponse(StageResponse(124, 2, 3));
        var responsePath = ResponsePath(options);
        var sourcePath = Path.Combine(directory.Path, "identical-stage-response.xml");
        File.WriteAllBytes(sourcePath, responseBytes);
        File.Copy(sourcePath, responsePath, overwrite: true);
        using var transport = CreateTransport(options);
        var request = StageRequest(124);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        Assert.False(File.Exists(responsePath));
        File.Copy(sourcePath, responsePath, overwrite: true);

        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(124, response.CorrelationId);
        Assert.Equal(2, response.CurrentStageX);
        Assert.Equal(3, response.CurrentStageY);
        Assert.False(File.Exists(responsePath));
    }

    [Fact]
    public async Task Exchange_PreExistingResponseDeleteFailureDoesNotBlockRequestOrLaterResponse()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var responsePath = ResponsePath(options);
        File.WriteAllBytes(responsePath, _codec.SerializeResponse(StageResponse(126, 1, 1)));
        using var responseLock = new FileStream(
            responsePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var transport = CreateTransport(options);
        var request = StageRequest(127);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        Assert.False(exchange.IsCompleted);

        responseLock.Dispose();
        PublishResponse(options, StageResponse(127, 6, 7));
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(127, response.CorrelationId);
        Assert.Equal(6, response.CurrentStageX);
        Assert.Equal(7, response.CurrentStageY);
    }

    [Fact]
    public async Task Exchange_DeleteFailureDoesNotAcceptUnchangedMatchingStaleResponse()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(180);
        var responsePath = ResponsePath(options);
        var responseBytes = _codec.SerializeResponse(StageResponse(128, 8, 9));
        File.WriteAllBytes(responsePath, responseBytes);
        using var responseLock = new FileStream(
            responsePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var transport = CreateTransport(options);
        var request = StageRequest(128);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        responseLock.Dispose();

        await Assert.ThrowsAsync<EquipmentResponseTimeoutException>(() => exchange);
        Assert.Equal(responseBytes, File.ReadAllBytes(responsePath));
    }

    [Fact]
    public async Task Exchange_RetryPublishesByteIdenticalRequestWithSameCorrelation()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromSeconds(1);
        options.RetryEnabled = true;
        options.MaximumRetryCount = 1;
        options.RetryDelay = TimeSpan.Zero;
        using var transport = CreateTransport(options);
        var request = StageRequest(107);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        var first = await WaitForRequestAsync(options, request);
        var firstWriteTimeUtc = File.GetLastWriteTimeUtc(RequestPath(options));
        var second = await WaitForRepublishedRequestAsync(
            options,
            request,
            firstWriteTimeUtc);
        Assert.Equal(first, second);

        PublishResponse(options, StageResponse(107, 0, 0));
        Assert.Equal(107, (await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(5))).CorrelationId);
    }

    [Fact]
    public async Task CanceledExchange_DeletesOnlyItsExactPublishedBytes()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var request = LiveRequest(108);

        var exchange = transport.ExchangeAsync(request, cancellation.Token);
        await WaitForRequestAsync(options, request);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);

        await WaitForFileAbsentAsync(RequestPath(options));
        Assert.False(File.Exists(RequestPath(options)));
    }

    [Fact]
    public async Task CanceledExchange_CleanupUsesTheExchangeSettingsSnapshot()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        var originalStableReadDelay = options.StableReadDelay;
        var stableReader = new RecordingStableFileReader();
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance,
            _codec,
            new RecordingTraceSink(),
            () => DateTime.UtcNow,
            stableReader);
        using var cancellation = new CancellationTokenSource();
        var request = LiveRequest(142);

        var exchange = transport.ExchangeAsync(request, cancellation.Token);
        await WaitForRequestAsync(options, request);
        stableReader.ClearObservations();

        // Detached cancellation cleanup belongs to the exchange that published these exact
        // bytes. A later UI edit must not replace its stable-read timing with the new value.
        options.StableReadDelay = TimeSpan.FromSeconds(5);
        options.PollingInterval = TimeSpan.FromSeconds(5);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await WaitForFileAbsentAsync(RequestPath(options));

        Assert.NotEmpty(stableReader.ObservedStableReadDelays);
        Assert.All(
            stableReader.ObservedStableReadDelays,
            delay => Assert.Equal(originalStableReadDelay, delay));
    }

    [Fact]
    public async Task CanceledExchange_PreservesAReplacedRequestOwnedByAnotherWriter()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        using var cancellation = new CancellationTokenSource();
        var request = LiveRequest(109);

        var exchange = transport.ExchangeAsync(request, cancellation.Token);
        await WaitForRequestAsync(options, request);
        var replacement = _codec.SerializeRequest(
            new EquipmentRequestMessage(999, EquipmentActionNames.Abort));
        File.WriteAllBytes(RequestPath(options), replacement);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        await Task.Delay(250);

        Assert.Equal(replacement, File.ReadAllBytes(RequestPath(options)));
    }

    [Fact]
    public async Task InvalidResponseTimesOutWithoutDeletingRequest()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(180);
        options.ApplicationRequestLifecycle = ApplicationRequestFileLifecycle.RetainUntilOverwritten;
        var logger = new ResponseRejectionLogger();
        using var transport = CreateTransport(options, logger);
        var request = StageRequest(110);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        File.WriteAllText(ResponsePath(options), "<not-the-contract />", new UTF8Encoding(false));

        await Assert.ThrowsAsync<EquipmentResponseTimeoutException>(() => exchange);
        Assert.True(File.Exists(RequestPath(options)));
        var warning = await logger.WarningLogged.Task.WithTimeoutAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("pending stage request 110", warning, StringComparison.Ordinal);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeAllocationAndTimesOutSafely()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(180);
        options.ApplicationRequestLifecycle = ApplicationRequestFileLifecycle.RetainUntilOverwritten;
        using var transport = CreateTransport(options);
        var request = StageRequest(115);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        using (var stream = new FileStream(
                   ResponsePath(options),
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength((long)EquipmentMessageLimits.MaximumWirePayloadBytes + 1L);
        }

        await Assert.ThrowsAsync<EquipmentResponseTimeoutException>(() => exchange);
        Assert.True(File.Exists(RequestPath(options)));
        Assert.Equal(
            (long)EquipmentMessageLimits.MaximumWirePayloadBytes + 1L,
            new FileInfo(ResponsePath(options)).Length);
    }

    [Fact]
    public async Task ExchangeGate_SerializesTwoCallersUsingSharedFileNames()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var firstRequest = new EquipmentRequestMessage(111, EquipmentActionNames.Abort);
        var secondRequest = new EquipmentRequestMessage(112, EquipmentActionNames.Abort);

        var first = transport.ExchangeAsync(firstRequest, CancellationToken.None);
        await WaitForRequestAsync(options, firstRequest);
        var second = transport.ExchangeAsync(secondRequest, CancellationToken.None);
        await Task.Delay(80);
        Assert.True(_codec.TryDeserializeRequest(File.ReadAllBytes(RequestPath(options)), out var active));
        Assert.Equal(111, active!.CorrelationId);

        PublishResponse(options, new EquipmentResponseMessage(111, EquipmentActionNames.Abort, 0));
        await first.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        await WaitForRequestAsync(options, secondRequest);
        PublishResponse(options, new EquipmentResponseMessage(112, EquipmentActionNames.Abort, 0));
        Assert.Equal(112, (await second.WithTimeoutAsync(TimeSpan.FromSeconds(3))).CorrelationId);
    }

    [Fact]
    public async Task EquipmentDeleteMode_WaitsForEquipmentToRemoveRequestBeforeCompleting()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        using var transport = CreateTransport(options);
        var request = StageRequest(113);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, StageResponse(113, 0, 0));
        await Task.Delay(80);
        Assert.False(exchange.IsCompleted);

        File.Delete(RequestPath(options));
        Assert.Equal(113, (await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3))).CorrelationId);
    }

    [Fact]
    public async Task LiveResponseCleanupLockDoesNotTurnValidFrameIntoTimeout()
    {
        using var directory = new TempDirectory();
        var options = CreateOptions(directory.Path);
        using var transport = CreateTransport(options);
        var request = LiveRequest(114);

        var exchange = transport.ExchangeAsync(request, CancellationToken.None);
        await WaitForRequestAsync(options, request);
        PublishResponse(options, LiveResponse(114, @"C:\Images\frame.png"));
        using var responseLock = new FileStream(
            ResponsePath(options), FileMode.Open, FileAccess.Read, FileShare.Read);
        var elapsed = Stopwatch.StartNew();
        var response = await exchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));
        elapsed.Stop();

        Assert.Equal(114, response.CorrelationId);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Exchange_UsesOneImmutableSettingsSnapshotAndNextExchangeUsesLatestSettings()
    {
        using var originalDirectory = new TempDirectory();
        using var updatedDirectory = new TempDirectory();
        var options = CreateOptions(originalDirectory.Path);
        var originalPaths = CreateOptions(originalDirectory.Path);
        var stableReader = new RecordingStableFileReader();
        using var transport = new FileEquipmentTransport(
            Options.Create(options),
            NullLogger<FileEquipmentTransport>.Instance,
            _codec,
            new RecordingTraceSink(),
            () => DateTime.UtcNow,
            stableReader);
        var firstRequest = StageRequest(140);

        var firstExchange = transport.ExchangeAsync(firstRequest, CancellationToken.None);
        await WaitForRequestAsync(originalPaths, firstRequest);

        // Mutate every settings family after publication. The active exchange must continue with
        // its already-validated snapshot rather than mixing these values into response waiting or
        // cleanup. Deliberately long timings make any leaked StableReadDelay/PollingInterval
        // observable as a test timeout.
        options.ExchangeDirectory = updatedDirectory.Path;
        options.LiveImageDirectory = Path.Combine(updatedDirectory.Path, "live-updated");
        options.RequestFileName = "updated.request.xml";
        options.ResponseFileName = "updated.response.xml";
        options.EquipmentRequestLifecycle = EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead;
        options.ApplicationRequestLifecycle = ApplicationRequestFileLifecycle.RetainUntilOverwritten;
        options.ApplicationResponseLifecycle = ApplicationResponseFileLifecycle.RetainUntilOverwritten;
        options.ResponseTimeout = TimeSpan.FromSeconds(5);
        options.RetryEnabled = true;
        options.MaximumRetryCount = 2;
        options.RetryDelay = TimeSpan.FromSeconds(2);
        options.RequestPublishDelay = TimeSpan.FromSeconds(2);
        options.PollingInterval = TimeSpan.FromSeconds(2);
        options.StableReadDelay = TimeSpan.FromSeconds(2);

        PublishResponse(originalPaths, StageResponse(140, 1, 2));
        var firstResponse = await firstExchange.WithTimeoutAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(140, firstResponse.CorrelationId);
        Assert.False(File.Exists(RequestPath(originalPaths)));
        Assert.False(File.Exists(ResponsePath(originalPaths)));
        Assert.NotEmpty(stableReader.ObservedStableReadDelays);
        Assert.All(
            stableReader.ObservedStableReadDelays,
            delay => Assert.Equal(originalPaths.StableReadDelay, delay));

        // Keep the updated paths and lifecycle policy, but use practical timings for the next
        // exchange. Capturing again must observe these latest values.
        options.ResponseTimeout = TimeSpan.FromSeconds(2);
        options.RetryEnabled = false;
        options.MaximumRetryCount = 1;
        options.RetryDelay = TimeSpan.FromMilliseconds(25);
        options.RequestPublishDelay = TimeSpan.Zero;
        options.PollingInterval = TimeSpan.FromMilliseconds(25);
        options.StableReadDelay = TimeSpan.FromMilliseconds(25);
        stableReader.ClearObservations();

        var secondRequest = StageRequest(141);
        var secondExchange = transport.ExchangeAsync(secondRequest, CancellationToken.None);
        await WaitForRequestAsync(options, secondRequest);
        Assert.False(File.Exists(RequestPath(originalPaths)));

        PublishResponse(options, StageResponse(141, 3, 4));
        File.Delete(RequestPath(options));
        var secondResponse = await secondExchange.WithTimeoutAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(141, secondResponse.CorrelationId);
        Assert.True(File.Exists(ResponsePath(options)));
        Assert.NotEmpty(stableReader.ObservedStableReadDelays);
        Assert.All(
            stableReader.ObservedStableReadDelays,
            delay => Assert.Equal(options.StableReadDelay, delay));
    }

    private FileEquipmentTransport CreateTransport(
        EquipmentCommunicationOptions options,
        ILogger<FileEquipmentTransport>? logger = null,
        IEquipmentExchangeTraceSink? traceSink = null)
    {
        var effectiveLogger = logger ?? NullLogger<FileEquipmentTransport>.Instance;
        return traceSink is null
            ? new FileEquipmentTransport(Options.Create(options), effectiveLogger, _codec)
            : new FileEquipmentTransport(
                Options.Create(options),
                effectiveLogger,
                _codec,
                traceSink);
    }

    private async Task<byte[]> WaitForRequestAsync(
        EquipmentCommunicationOptions options,
        EquipmentRequestMessage expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(RequestPath(options)))
                {
                    var bytes = File.ReadAllBytes(RequestPath(options));
                    if (_codec.TryDeserializeRequest(bytes, out var request)
                        && request!.CorrelationId == expected.CorrelationId
                        && request.Action == expected.Action)
                    {
                        return bytes;
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected equipment request was not published.");
    }

    private static async Task WaitForFileAbsentAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The canceled request was not cleaned up.");
    }

    private static async Task WaitForFileExistsAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected file was not created.");
    }

    private async Task<byte[]> WaitForRepublishedRequestAsync(
        EquipmentCommunicationOptions options,
        EquipmentRequestMessage expected,
        DateTime firstWriteTimeUtc)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var path = RequestPath(options);
                if (File.Exists(path)
                    && File.GetLastWriteTimeUtc(path) != firstWriteTimeUtc)
                {
                    var bytes = File.ReadAllBytes(path);
                    if (_codec.TryDeserializeRequest(bytes, out var parsed)
                        && parsed!.CorrelationId == expected.CorrelationId
                        && string.Equals(
                            parsed.Action,
                            expected.Action,
                            StringComparison.Ordinal))
                    {
                        return bytes;
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The equipment request was not republished.");
    }

    private void PublishResponse(
        EquipmentCommunicationOptions options,
        EquipmentResponseMessage response)
    {
        var path = ResponsePath(options);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, _codec.SerializeResponse(response));
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static void PublishRawResponse(
        EquipmentCommunicationOptions options,
        string xml)
    {
        var path = ResponsePath(options);
        var temp = path + ".tmp";
        File.WriteAllText(temp, xml, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private static EquipmentRequestMessage StageRequest(
        int correlationId,
        double x = 0,
        double y = 0)
    {
        return new EquipmentRequestMessage(
            correlationId,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = x,
                ["stage_y"] = y
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
                ["image_path"] = @"C:\Images\requested.png"
            });
    }

    private static EquipmentResponseMessage StageResponse(
        int correlationId,
        double x,
        double y)
    {
        return new EquipmentResponseMessage(
            correlationId,
            EquipmentActionNames.Stage,
            0,
            new Dictionary<string, object?>
            {
                ["current_stage_x"] = x,
                ["current_stage_y"] = y
            });
    }

    private static EquipmentResponseMessage CameraResponse(int correlationId)
    {
        return new EquipmentResponseMessage(
            correlationId,
            EquipmentActionNames.Camera,
            0,
            new Dictionary<string, object?>
            {
                ["current_camera_x"] = 0d,
                ["current_camera_y"] = 0d
            });
    }

    private static EquipmentResponseMessage LiveResponse(int correlationId, string path)
    {
        return new EquipmentResponseMessage(
            correlationId,
            EquipmentActionNames.Live,
            0,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = 1,
                ["image_path"] = path
            });
    }

    private static EquipmentCommunicationOptions CreateOptions(string directory)
    {
        return new EquipmentCommunicationOptions
        {
            ExchangeDirectory = directory,
            RequestFileName = "equipment.request.xml",
            ResponseFileName = "equipment.response.xml",
            ResponseTimeout = TimeSpan.FromSeconds(2),
            RequestPublishDelay = TimeSpan.Zero,
            PollingInterval = TimeSpan.FromMilliseconds(10),
            StableReadDelay = TimeSpan.FromMilliseconds(10),
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };
    }

    private static string RequestPath(EquipmentCommunicationOptions options) =>
        Path.Combine(options.ExchangeDirectory, options.RequestFileName);

    private static string ResponsePath(EquipmentCommunicationOptions options) =>
        Path.Combine(options.ExchangeDirectory, options.ResponseFileName);

    private sealed class ResponseRejectionLogger : ILogger<FileEquipmentTransport>
    {
        private int _warningCount;

        public TaskCompletionSource<string> WarningLogged { get; } = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int WarningCount => Volatile.Read(ref _warningCount);

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
            if (logLevel != LogLevel.Warning)
            {
                return;
            }

            Interlocked.Increment(ref _warningCount);
            WarningLogged.TrySetResult(formatter(state, exception));
        }

        private sealed class EmptyScope : IDisposable
        {
            public static EmptyScope Instance { get; } = new EmptyScope();

            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingTraceSink : IEquipmentExchangeTraceSink
    {
        private readonly object _gate = new object();
        private readonly List<string> _notificationOrder = new List<string>();

        public TaskCompletionSource<PublishedRequestTrace> RequestPublished { get; } =
            new TaskCompletionSource<PublishedRequestTrace>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<MatchedResponseTrace> ResponseMatched { get; } =
            new TaskCompletionSource<MatchedResponseTrace>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> NotificationOrder
        {
            get
            {
                lock (_gate)
                {
                    return _notificationOrder.ToArray();
                }
            }
        }

        public void OnRequestPublished(
            string filePath,
            EquipmentRequestMessage request,
            int attempt)
        {
            lock (_gate)
            {
                _notificationOrder.Add("request");
            }

            RequestPublished.TrySetResult(new PublishedRequestTrace(filePath, request, attempt));
        }

        public void OnResponseMatched(
            string filePath,
            EquipmentResponseMessage response)
        {
            lock (_gate)
            {
                _notificationOrder.Add("response");
            }

            ResponseMatched.TrySetResult(new MatchedResponseTrace(filePath, response));
        }

        public void OnExchangeStopped(
            string filePath,
            EquipmentRequestMessage request,
            string reason)
        {
        }
    }

    private sealed class ThrowingTraceSink : IEquipmentExchangeTraceSink
    {
        private int _requestPublishedCount;
        private int _responseMatchedCount;
        private int _exchangeStoppedCount;

        public int RequestPublishedCount => Volatile.Read(ref _requestPublishedCount);

        public int ResponseMatchedCount => Volatile.Read(ref _responseMatchedCount);

        public int ExchangeStoppedCount => Volatile.Read(ref _exchangeStoppedCount);

        public void OnRequestPublished(
            string filePath,
            EquipmentRequestMessage request,
            int attempt)
        {
            Interlocked.Increment(ref _requestPublishedCount);
            throw new InvalidOperationException("request trace failure");
        }

        public void OnResponseMatched(
            string filePath,
            EquipmentResponseMessage response)
        {
            Interlocked.Increment(ref _responseMatchedCount);
            throw new InvalidOperationException("response trace failure");
        }

        public void OnExchangeStopped(
            string filePath,
            EquipmentRequestMessage request,
            string reason)
        {
            Interlocked.Increment(ref _exchangeStoppedCount);
            throw new InvalidOperationException("stopped trace failure");
        }
    }

    private sealed class RecordingStableFileReader : IStableEquipmentFileReader
    {
        private readonly object _sync = new object();
        private readonly StableEquipmentFileReader _inner = new StableEquipmentFileReader();
        private readonly List<TimeSpan> _observedStableReadDelays = new List<TimeSpan>();

        public IReadOnlyList<TimeSpan> ObservedStableReadDelays
        {
            get
            {
                lock (_sync)
                {
                    return _observedStableReadDelays.ToArray();
                }
            }
        }

        public EquipmentFilePresence GetPresence(string filePath)
        {
            return _inner.GetPresence(filePath);
        }

        public Task<byte[]?> TryReadAsync(
            string filePath,
            TimeSpan stableReadDelay,
            int maximumPayloadBytes,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _observedStableReadDelays.Add(stableReadDelay);
            }

            return _inner.TryReadAsync(
                filePath,
                stableReadDelay,
                maximumPayloadBytes,
                cancellationToken);
        }

        public void ClearObservations()
        {
            lock (_sync)
            {
                _observedStableReadDelays.Clear();
            }
        }
    }

    private sealed class PublishedRequestTrace
    {
        public PublishedRequestTrace(
            string filePath,
            EquipmentRequestMessage request,
            int attempt)
        {
            FilePath = filePath;
            Request = request;
            Attempt = attempt;
        }

        public string FilePath { get; }

        public EquipmentRequestMessage Request { get; }

        public int Attempt { get; }
    }

    private sealed class MatchedResponseTrace
    {
        public MatchedResponseTrace(string filePath, EquipmentResponseMessage response)
        {
            FilePath = filePath;
            Response = response;
        }

        public string FilePath { get; }

        public EquipmentResponseMessage Response { get; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DrillFlow-TransportTests-" + Guid.NewGuid().ToString("N"));
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
