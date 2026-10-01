using System;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Infrastructure.Communication.FileExchange;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureEquipmentFileReadBudgetTests
{
    [Fact]
    public async Task Timeout_CancelsStabilityWaitWithoutCancelingTheOperator()
    {
        using var operatorCancellation = new CancellationTokenSource();
        var reader = new ControlledReader();
        var read = EquipmentFileReadBudget.TryReadAsync(reader, "response.xml",
            TimeSpan.FromSeconds(5), 1024, TimeSpan.FromMilliseconds(50), operatorCancellation.Token);

        Assert.Null(await read.WithTimeoutAsync(TimeSpan.FromSeconds(1)));
        Assert.True(reader.ReadToken.IsCancellationRequested);
        Assert.False(operatorCancellation.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(5), reader.StableReadDelay);
    }

    [Fact]
    public async Task OperatorCancellation_PropagatesToTheCallerAndReader()
    {
        using var operatorCancellation = new CancellationTokenSource();
        var reader = new ControlledReader();
        var read = EquipmentFileReadBudget.TryReadAsync(reader, "response.xml",
            TimeSpan.FromSeconds(5), 1024, TimeSpan.FromSeconds(5), operatorCancellation.Token);
        operatorCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(reader.ReadToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CompletedSampleWithinBudget_ReturnsTheOriginalBytes()
    {
        var reader = new ControlledReader();
        var expected = new byte[] { 1, 2, 3 };
        reader.Completion.SetResult(expected);

        var actual = await EquipmentFileReadBudget.TryReadAsync(reader, "response.xml",
            TimeSpan.Zero, 1024, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ExpiredBudget_DoesNotStartAnotherRead()
    {
        var reader = new ControlledReader();

        Assert.Null(await EquipmentFileReadBudget.TryReadAsync(reader, "response.xml",
            TimeSpan.FromSeconds(5), 1024, TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(0, reader.Calls);
    }

    private sealed class ControlledReader : IStableEquipmentFileReader
    {
        public TaskCompletionSource<byte[]?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }
        public TimeSpan StableReadDelay { get; private set; }
        public int Calls { get; private set; }

        public EquipmentFilePresence GetPresence(string path) => EquipmentFilePresence.Present;

        public async Task<byte[]?> TryReadAsync(
            string path,
            TimeSpan stableReadDelay,
            int maximumPayloadBytes,
            CancellationToken cancellationToken)
        {
            Calls++;
            ReadToken = cancellationToken;
            StableReadDelay = stableReadDelay;
            using (cancellationToken.Register(() => Completion.TrySetCanceled()))
            {
                return await Completion.Task;
            }
        }
    }
}
