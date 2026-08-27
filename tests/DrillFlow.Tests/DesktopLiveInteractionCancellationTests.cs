using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Desktop.ViewModels;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopLiveInteractionCancellationTests
{
    [Fact]
    public void PostResponseToken_IsCanceledByStreamLifecycle()
    {
        using (var stream = new CancellationTokenSource())
        using (var shutdown = new CancellationTokenSource())
        using (var linked = LiveInteractionCancellation.CreatePostResponseSource(
                   stream.Token,
                   shutdown.Token))
        {
            stream.Cancel();

            Assert.True(linked.IsCancellationRequested);
        }
    }

    [Fact]
    public void PostResponseToken_IsCanceledByApplicationShutdown()
    {
        using (var stream = new CancellationTokenSource())
        using (var shutdown = new CancellationTokenSource())
        using (var linked = LiveInteractionCancellation.CreatePostResponseSource(
                   stream.Token,
                   shutdown.Token))
        {
            shutdown.Cancel();

            Assert.True(linked.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(500, 1000)]
    [InlineData(2500, 2500)]
    public void ImageIoBudget_UsesConfiguredTimeoutWithOneSecondMinimum(
        int configuredMilliseconds,
        int expectedMilliseconds)
    {
        var normalized = LiveImageIoTimeout.NormalizeBudget(
            TimeSpan.FromMilliseconds(configuredMilliseconds));

        Assert.Equal(expectedMilliseconds, normalized.TotalMilliseconds);
    }

    [Fact]
    public void ImageIoTimeout_DoesNotMaskNavigationCancellation()
    {
        using (var timeout = new CancellationTokenSource())
        using (var lifecycle = new CancellationTokenSource())
        {
            timeout.Cancel();
            lifecycle.Cancel();

            Assert.False(LiveImageIoTimeout.IsTimeout(timeout, lifecycle.Token));
        }
    }

    [Fact]
    public async Task ShutdownDrain_ReturnsWithoutWaitingForBlockedOperationIndefinitely()
    {
        var blocked = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = Stopwatch.StartNew();

        var completed = await LiveInteractionShutdownDrain.WaitForCompletionAsync(
            blocked.Task,
            TimeSpan.FromMilliseconds(40));

        elapsed.Stop();
        Assert.False(completed);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        blocked.SetResult(null);
    }

    [Fact]
    public async Task ShutdownDrain_RecognizesAlreadyCompletedOperation()
    {
        Assert.True(
            await LiveInteractionShutdownDrain.WaitForCompletionAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)));
    }
}
