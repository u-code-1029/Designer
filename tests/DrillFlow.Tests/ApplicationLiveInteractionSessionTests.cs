using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Application.LiveInteraction;
using DrillFlow.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationLiveInteractionSessionTests
{
    [Fact]
    public async Task Commands_UseCanonicalContractsAndUniqueCorrelations()
    {
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(Response(
                request,
                request.Command == LiveInteractionProtocol.MoveCommand
                    ? null
                    : @"C:\camera\image.png")));
        using var session = CreateSession(transport);

        var frame = await session.RequestFrameAsync(12.5E-3);
        var move = await session.MoveRelativeAsync(2.5E-4, -3E-4);
        var capture = await session.CaptureAsync();

        Assert.Equal(new[] { 1, 2, 3 }, transport.Requests.Select(item => item.Index));
        Assert.Equal(
            new[]
            {
                LiveInteractionProtocol.FrameCommand,
                LiveInteractionProtocol.MoveCommand,
                LiveInteractionProtocol.CaptureCommand
            },
            transport.Requests.Select(item => item.Command));

        Assert.Equal(
            12.5E-3,
            transport.Requests[0].Parameters[
                LiveInteractionProtocol.HorizontalFieldWidthParameter]);
        Assert.Equal(
            LiveInteractionProtocol.RelativeMoveMode,
            transport.Requests[1].Parameters[LiveInteractionProtocol.MoveModeParameter]);
        Assert.Equal(2.5E-4, transport.Requests[1].Parameters[LiveInteractionProtocol.MoveXParameter]);
        Assert.Equal(-3E-4, transport.Requests[1].Parameters[LiveInteractionProtocol.MoveYParameter]);
        Assert.Empty(transport.Requests[2].Parameters);

        Assert.Equal(@"C:\camera\image.png", frame.ImagePath);
        Assert.Null(move.ImagePath);
        Assert.Equal(@"C:\camera\image.png", capture.ImagePath);
    }

    [Theory]
    [InlineData("frame")]
    [InlineData("capture")]
    public async Task ImageCommands_RequireImagePathInResponse(string command)
    {
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(Response(request)));
        using var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            command == "frame"
                ? session.RequestFrameAsync(10E-3)
                : session.CaptureAsync());

        Assert.Contains("image_path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN, 0d)]
    [InlineData(double.PositiveInfinity, 0d)]
    [InlineData(0d, double.NegativeInfinity)]
    [InlineData(-0.5d, 0d)]
    [InlineData(0.5d, 0d)]
    [InlineData(0d, -0.5d)]
    [InlineData(0d, 0.5d)]
    public void MoveRelative_RejectsNonFiniteOrOutOfRangeOffsetsBeforeExchange(
        double moveX,
        double moveY)
    {
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(Response(request)));
        using var session = CreateSession(transport);

        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.MoveRelativeAsync(moveX, moveY);
        });
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1E-3)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Frame_RejectsNonPositiveOrNonFiniteHfwBeforeExchange(double hfw)
    {
        var transport = new RecordingTransport((request, _) =>
            Task.FromResult(Response(request, @"C:\camera\frame.png")));
        using var session = CreateSession(transport);

        Assert.Throws<ParameterValidationException>(() =>
        {
            _ = session.RequestFrameAsync(hfw);
        });
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ConcurrentCalls_AreSerializedForTheWholeRequestResponseExchange()
    {
        var firstObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResponse = new TaskCompletionSource<EquipmentResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport((request, _) =>
        {
            if (request.Index == 1)
            {
                firstObserved.TrySetResult(true);
                return firstResponse.Task;
            }

            return Task.FromResult(Response(request, @"C:\camera\capture.png"));
        });
        using var session = CreateSession(transport);
        var busyStates = new List<bool>();
        session.BusyChanged += (_, _) => busyStates.Add(session.IsBusy);

        var first = session.RequestFrameAsync(10E-3);
        await firstObserved.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        Assert.True(session.IsBusy);
        var second = session.CaptureAsync();
        await Task.Delay(50);

        Assert.Single(transport.Requests);
        Assert.False(second.IsCompleted);

        firstResponse.SetResult(Response(transport.Requests[0], @"C:\camera\frame.png"));
        await first;
        await second;

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(2, transport.Requests[1].Index);
        Assert.False(session.IsBusy);
        Assert.Equal(new[] { true, false, true, false }, busyStates);
    }

    [Fact]
    public async Task ResponseCorrelationMustMatchAllocatedRequest()
    {
        var transport = new RecordingTransport((request, _) => Task.FromResult(
            new EquipmentResponseMessage(
                request.Index + 1,
                "return",
                new Dictionary<string, object?>
                {
                    ["stage_x"] = 0d,
                    ["stage_y"] = 0d,
                    ["image_path"] = @"C:\camera\frame.png"
                })));
        using var session = CreateSession(transport);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RequestFrameAsync(10E-3));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowingBusyObserver_DoesNotBreakExchangeOrStrandSessionGate()
    {
        var transport = new RecordingTransport((request, _) => Task.FromResult(
            Response(request, @"C:\camera\frame.png")));
        using var session = CreateSession(transport);
        var notifications = 0;
        session.BusyChanged += (_, _) => throw new InvalidOperationException("Observer failed.");
        session.BusyChanged += (_, _) => Interlocked.Increment(ref notifications);

        await session.RequestFrameAsync(10E-3);
        await session.CaptureAsync();

        Assert.False(session.IsBusy);
        Assert.Equal(4, notifications);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task DisposeDuringExchange_AllowsInflightFinallyToCompleteAndRejectsNewCalls()
    {
        var observed = new TaskCompletionSource<EquipmentRequestMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<EquipmentResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport((request, _) =>
        {
            observed.TrySetResult(request);
            return response.Task;
        });
        var session = CreateSession(transport);

        var exchange = session.RequestFrameAsync(10E-3);
        var request = await observed.Task.WithTimeoutAsync(TimeSpan.FromSeconds(2));
        session.Dispose();
        response.SetResult(Response(request, @"C:\camera\frame.png"));

        Assert.Equal(request.Index, (await exchange).Index);
        Assert.False(session.IsBusy);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.RequestFrameAsync(10E-3));
    }

    private static LiveInteractionSession CreateSession(IEquipmentFileTransport transport)
    {
        return new LiveInteractionSession(
            transport,
            new IncrementingCorrelationProvider(),
            NullLogger<LiveInteractionSession>.Instance);
    }

    private static EquipmentResponseMessage Response(
        EquipmentRequestMessage request,
        string? imagePath = null)
    {
        var properties = new Dictionary<string, object?>
        {
            ["stage_x"] = request.Index * 1E-3,
            ["stage_y"] = request.Index * -1E-3
        };
        if (imagePath != null)
        {
            properties["image_path"] = imagePath;
        }

        return new EquipmentResponseMessage(request.Index, "return", properties);
    }

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

        public List<EquipmentRequestMessage> Requests { get; } =
            new List<EquipmentRequestMessage>();

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
}
