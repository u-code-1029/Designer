using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationLiveInteractionSessionTests
{
    [Fact]
    public async Task Commands_UseCanonicalActionsParametersAndUniqueCorrelations()
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(ResponseFor(request)));
        using var session = CreateSession(transport, directory.Path);

        var live = await session.RequestFrameAsync(1.2E-3);
        var stage = await session.MoveStageAsync("relative", 2.5E-4, -3E-4);
        var camera = await session.MoveCameraAsync("absolute", -4E-6, 8E-6);
        var focus = await session.FocusAsync(1E-3, 50E-6, 13);
        var lens = await session.ChangeLensAsync("no_change");
        var acb = await session.AutoContrastBrightnessAsync(7.5E-4);
        var integration = await session.IntegrateAsync(8E-4, 8);
        var om = await session.RequestOmImageAsync();

        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            transport.Requests.Select(item => item.CorrelationId));
        Assert.Equal(
            new[] { "live", "stage", "camera", "focus", "lens", "acb", "integration", "om" },
            transport.Requests.Select(item => item.Action));

        var liveRequest = transport.Requests[0];
        Assert.Equal(1.2E-3, liveRequest.Parameters["hfw"]);
        Assert.Equal(1, liveRequest.Parameters["frame_count"]);
        Assert.Equal(live.RequestedImagePath, liveRequest.Parameters["image_path"]);
        Assert.Equal(
            Path.Combine(directory.Path, ".drillflow-live", "live-1.bmp"),
            live.RequestedImagePath);

        Assert.Equal("relative", transport.Requests[1].Parameters["move_mode"]);
        Assert.Equal(2.5E-4, transport.Requests[1].Parameters["stage_x"]);
        Assert.Equal(-3E-4, transport.Requests[1].Parameters["stage_y"]);
        Assert.Equal("absolute", transport.Requests[2].Parameters["move_mode"]);
        Assert.Equal(-4E-6, transport.Requests[2].Parameters["camera_x"]);
        Assert.Equal(8E-6, transport.Requests[2].Parameters["camera_y"]);
        Assert.Equal(1E-3, transport.Requests[3].Parameters["hfw"]);
        Assert.Equal(50E-6, transport.Requests[3].Parameters["range"]);
        Assert.Equal(13, transport.Requests[3].Parameters["steps"]);
        Assert.Equal("no_change", transport.Requests[4].Parameters["lens_mode"]);
        Assert.Equal(7.5E-4, transport.Requests[5].Parameters["hfw"]);
        Assert.Equal(8E-4, transport.Requests[6].Parameters["hfw"]);
        Assert.Equal(8, transport.Requests[6].Parameters["frame_count"]);
        Assert.Equal(integration.RequestedImagePath, transport.Requests[6].Parameters["image_path"]);
        Assert.Single(transport.Requests[7].Parameters);
        Assert.Equal(om.RequestedImagePath, transport.Requests[7].Parameters["image_path"]);
        Assert.Equal(
            Path.Combine(directory.Path, ".drillflow-live", "om-8.bmp"),
            om.RequestedImagePath);

        Assert.Equal(2, stage.CorrelationId);
        Assert.Equal(3, camera.CorrelationId);
        Assert.Equal(4, focus.CorrelationId);
        Assert.Equal("lens1", lens.CurrentLensMode);
        Assert.Equal(6, acb.CorrelationId);
    }

    [Fact]
    public async Task RequestFrame_NormalizesForwardSlashExchangeDirectoryForImagePath()
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(ResponseFor(request)));
        using var session = CreateSession(transport, directory.Path.Replace('\\', '/'));

        var exchange = await session.RequestFrameAsync(1E-3);

        Assert.DoesNotContain("/", exchange.RequestedImagePath, StringComparison.Ordinal);
        Assert.True(EquipmentResponseMessage.IsSupportedAbsoluteImagePath(exchange.RequestedImagePath));
    }

    [Fact]
    public async Task ConfiguredLiveImageDirectory_IsUsedOnlyForUniqueLiveFramePaths()
    {
        using var directory = new LiveSessionTestDirectory();
        var configuredLiveDirectory = Path.Combine(directory.Path, "shared-live-frames");
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(ResponseFor(request)));
        using var session = CreateSession(
            transport,
            directory.Path,
            configuredLiveDirectory);

        var firstLive = await session.RequestFrameAsync(1E-3);
        var secondLive = await session.RequestFrameAsync(1E-3);
        var integration = await session.IntegrateAsync(1E-3, 4);
        var om = await session.RequestOmImageAsync();

        Assert.Equal(
            Path.Combine(configuredLiveDirectory, "live-1.bmp"),
            firstLive.RequestedImagePath);
        Assert.Equal(
            Path.Combine(configuredLiveDirectory, "live-2.bmp"),
            secondLive.RequestedImagePath);
        Assert.NotEqual(firstLive.RequestedImagePath, secondLive.RequestedImagePath);
        Assert.Equal(
            Path.Combine(directory.Path, ".drillflow-live", "integration-3.bmp"),
            integration.RequestedImagePath);
        Assert.Equal(
            Path.Combine(directory.Path, ".drillflow-live", "om-4.bmp"),
            om.RequestedImagePath);
        Assert.Equal(
            firstLive.RequestedImagePath,
            transport.Requests[0].Parameters["image_path"]);
        Assert.Equal(
            secondLive.RequestedImagePath,
            transport.Requests[1].Parameters["image_path"]);
        Assert.Equal(
            integration.RequestedImagePath,
            transport.Requests[2].Parameters["image_path"]);
        Assert.Equal(
            om.RequestedImagePath,
            transport.Requests[3].Parameters["image_path"]);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1E-3)]
    [InlineData(2.4E-3)]
    [InlineData(2.400001E-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Hfw_RejectsValuesOutsideStrictEquipmentRange(double hfw)
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(ResponseFor(request)));
        using var session = CreateSession(transport, directory.Path);

        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.RequestFrameAsync(hfw);
        });
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(65)]
    [InlineData(128)]
    public void Integration_RejectsNonPowerOfTwoOrOutOfRangeFrameCount(int frameCount)
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(ResponseFor(request)));
        using var session = CreateSession(transport, directory.Path);

        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.IntegrateAsync(1E-3, frameCount);
        });
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void MovesAndFocus_RejectMalformedValuesBeforeTransport()
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(ResponseFor(request)));
        using var session = CreateSession(transport, directory.Path);

        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.MoveStageAsync("RELATIVE", 0d, 0d);
        });
        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.MoveCameraAsync("relative", double.NaN, 0d);
        });
        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.FocusAsync(1E-3, 0d, 13);
        });
        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.FocusAsync(1E-3, 50E-6, 3);
        });
        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.ChangeLensAsync("Lens1");
        });
        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.AutoContrastBrightnessAsync(2.4E-3);
        });
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ResponseMustMatchCorrelationActionAndSuccessResult(
        bool wrongCorrelation,
        bool wrongAction)
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) => Task.FromResult(
            new EquipmentResponseMessage(
                wrongCorrelation ? request.CorrelationId + 1 : request.CorrelationId,
                wrongAction ? "camera" : request.Action,
                wrongCorrelation || wrongAction ? 0 : 1,
                StageProperties(0d, 0d))));
        using var session = CreateSession(transport, directory.Path);

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            session.MoveStageAsync("relative", 0d, 0d));

        Assert.True(
            error.Message.Contains("correlation", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("action", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailureResult_ThrowsExplicitEquipmentFailure()
    {
        using var directory = new LiveSessionTestDirectory();
        var transport = new RecordingTransport((request, _) => Task.FromResult(
            new EquipmentResponseMessage(
                request.CorrelationId,
                request.Action,
                1,
                StageProperties(0d, 0d))));
        using var session = CreateSession(transport, directory.Path);

        var error = await Assert.ThrowsAsync<LiveEquipmentActionFailedException>(() =>
            session.MoveStageAsync("relative", 0d, 0d));

        Assert.Equal(1, error.CorrelationId);
        Assert.Equal("stage", error.Action);
        Assert.Equal(1, error.Result);
    }

    [Fact]
    public async Task ImageExchangeCancellation_DeletesCorrelationOwnedRequestedPath()
    {
        using var directory = new LiveSessionTestDirectory();
        using var cancellation = new CancellationTokenSource();
        var requestPublished = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(async (request, cancellationToken) =>
        {
            var requestedPath = Assert.IsType<string>(request.Parameters["image_path"]);
            File.WriteAllBytes(requestedPath, new byte[] { 1, 2, 3 });
            requestPublished.TrySetResult(requestedPath);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A canceled exchange unexpectedly completed.");
        });
        using var session = CreateSession(transport, directory.Path);

        var exchange = session.RequestFrameAsync(1E-3, cancellation.Token);
        var ownedPath = await requestPublished.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exchange);
        Assert.False(File.Exists(ownedPath));
    }

    [Fact]
    public async Task ImageFailure_DeletesRequestedPathButPreservesAlternateResponsePath()
    {
        using var directory = new LiveSessionTestDirectory();
        var alternatePath = Path.Combine(directory.Path, "controller-owned.bmp");
        string? requestedPath = null;
        var transport = new RecordingTransport((request, _) =>
        {
            requestedPath = Assert.IsType<string>(request.Parameters["image_path"]);
            File.WriteAllBytes(requestedPath, new byte[] { 1 });
            File.WriteAllBytes(alternatePath, new byte[] { 2 });
            return Task.FromResult(new EquipmentResponseMessage(
                request.CorrelationId,
                request.Action,
                1,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["hfw"] = request.Parameters["hfw"],
                    ["frame_count"] = request.Parameters["frame_count"],
                    ["image_path"] = alternatePath,
                }));
        });
        using var session = CreateSession(transport, directory.Path);

        await Assert.ThrowsAsync<LiveEquipmentActionFailedException>(() =>
            session.RequestFrameAsync(1E-3));

        Assert.NotNull(requestedPath);
        Assert.False(File.Exists(requestedPath));
        Assert.True(File.Exists(alternatePath));
    }

    [Fact]
    public async Task InvalidImageResponse_DeletesCorrelationOwnedRequestedPath()
    {
        using var directory = new LiveSessionTestDirectory();
        string? requestedPath = null;
        var transport = new RecordingTransport((request, _) =>
        {
            requestedPath = Assert.IsType<string>(request.Parameters["image_path"]);
            File.WriteAllBytes(requestedPath, new byte[] { 1 });
            return Task.FromResult(new EquipmentResponseMessage(
                request.CorrelationId + 1,
                request.Action,
                0,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["hfw"] = request.Parameters["hfw"],
                    ["frame_count"] = request.Parameters["frame_count"],
                    ["image_path"] = requestedPath,
                }));
        });
        using var session = CreateSession(transport, directory.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RequestFrameAsync(1E-3));

        Assert.NotNull(requestedPath);
        Assert.False(File.Exists(requestedPath));
    }

    [Fact]
    public async Task ConcurrentCalls_AreSerializedForWholeExchange()
    {
        using var directory = new LiveSessionTestDirectory();
        var firstObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource<EquipmentResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport((request, _) =>
        {
            if (request.CorrelationId == 1)
            {
                firstObserved.TrySetResult(true);
                return firstResponse.Task;
            }

            return Task.FromResult(ResponseFor(request));
        });
        using var session = CreateSession(transport, directory.Path);
        var busyStates = new List<bool>();
        session.BusyChanged += (_, _) => busyStates.Add(session.IsBusy);

        var first = session.RequestFrameAsync(1E-3);
        await firstObserved.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        var second = session.MoveStageAsync("relative", 1E-3, 0d);
        await Task.Delay(50);

        Assert.Single(transport.Requests);
        Assert.False(second.IsCompleted);
        firstResponse.SetResult(ResponseFor(transport.Requests[0]));
        await first;
        await second;

        Assert.Equal(2, transport.Requests.Count);
        Assert.False(session.IsBusy);
        Assert.Equal(new[] { true, false, true, false }, busyStates);
    }

    private static LiveInteractionSession CreateSession(
        IEquipmentFileTransport transport,
        string exchangeDirectory,
        string? liveImageDirectory = null)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = exchangeDirectory,
            RequestFileName = "request.xml",
            ResponseFileName = "response.xml",
        };
        if (liveImageDirectory is not null)
        {
            options.LiveImageDirectory = liveImageDirectory;
        }

        return new LiveInteractionSession(
            transport,
            new IncrementingCorrelationProvider(),
            Options.Create(options),
            NullLogger<LiveInteractionSession>.Instance);
    }

    private static EquipmentResponseMessage ResponseFor(EquipmentRequestMessage request)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (request.Action)
        {
            case "stage":
                properties["current_stage_x"] = request.Parameters["stage_x"];
                properties["current_stage_y"] = request.Parameters["stage_y"];
                break;
            case "camera":
                properties["current_camera_x"] = request.Parameters["camera_x"];
                properties["current_camera_y"] = request.Parameters["camera_y"];
                break;
            case "focus":
                properties["z_to_sharpness_2d"] = new object?[]
                {
                    new object?[] { 0.1d, 500d },
                    new object?[] { 1.5d, 600d },
                };
                break;
            case "lens":
                properties["current_lens_mode"] = "lens1";
                break;
            case "live":
            case "integration":
                properties["hfw"] = request.Parameters["hfw"];
                properties["frame_count"] = request.Parameters["frame_count"];
                properties["image_path"] = request.Parameters["image_path"];
                break;
            case "om":
                properties["image_path"] = request.Parameters["image_path"];
                break;
        }

        return new EquipmentResponseMessage(
            request.CorrelationId,
            request.Action,
            0,
            properties);
    }

    private static Dictionary<string, object?> StageProperties(double x, double y) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["current_stage_x"] = x,
            ["current_stage_y"] = y,
        };

    private sealed class IncrementingCorrelationProvider : ICorrelationIdProvider
    {
        private int _value;

        public Task<int> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref _value));
        }
    }

    private sealed class RecordingTransport : IEquipmentFileTransport
    {
        private readonly Func<EquipmentRequestMessage, CancellationToken,
            Task<EquipmentResponseMessage>> _exchange;

        public RecordingTransport(
            Func<EquipmentRequestMessage, CancellationToken, Task<EquipmentResponseMessage>> exchange)
        {
            _exchange = exchange;
        }

        public List<EquipmentRequestMessage> Requests { get; } = new();

        public Task<EquipmentResponseMessage> ExchangeAsync(
            EquipmentRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return _exchange(request, cancellationToken);
        }
    }

    private sealed class LiveSessionTestDirectory : IDisposable
    {
        public LiveSessionTestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DrillFlow.LiveSessionTests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
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
