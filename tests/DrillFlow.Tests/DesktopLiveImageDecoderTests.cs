using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopLiveImageDecoderTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task DecodeAsync_DecodesAndFreezesImageOnDedicatedStaThread()
    {
        using (var decoder = new LiveImageDecoder(NullLogger<LiveImageDecoder>.Instance))
        {
            var result = await decoder.DecodeAsync(
                Convert.FromBase64String(OnePixelPngBase64),
                CancellationToken.None);

            Assert.Equal(1, result.OriginalPixelWidth);
            Assert.Equal(1, result.OriginalPixelHeight);
            Assert.True(result.ImageSource.IsFrozen);
            Assert.Equal(".png", result.DetectedFileExtension, ignoreCase: true);
        }
    }

    [Fact]
    public void SafetyLimits_RejectOversizedEncodedFile()
    {
        var exception = Assert.Throws<LiveImageLimitExceededException>(() =>
            LiveImageSafetyLimits.ValidateEncodedByteLength(
                LiveImageSafetyLimits.MaximumEncodedBytes + 1));

        Assert.Contains("64 MiB", exception.Message);
    }

    [Theory]
    [InlineData(32769, 1)]
    [InlineData(20000, 6000)]
    public void SafetyLimits_RejectOversizedPixelDimensions(int width, int height)
    {
        var exception = Assert.Throws<LiveImageLimitExceededException>(() =>
            LiveImageSafetyLimits.ValidatePixelDimensions(width, height));

        Assert.Contains("safe limits", exception.Message);
    }

    [Fact]
    public async Task DecodeAsync_RejectsValidBitmapWhoseAxisExceedsSafetyLimit()
    {
        using (var decoder = new LiveImageDecoder(NullLogger<LiveImageDecoder>.Instance))
        {
            var oversized = CreateBgr24Bitmap(
                LiveImageSafetyLimits.MaximumPixelDimension + 1,
                1);

            var exception = await Assert.ThrowsAsync<LiveImageLimitExceededException>(() =>
                decoder.DecodeAsync(oversized, CancellationToken.None));

            Assert.Contains("safe limits", exception.Message);
        }
    }

    [Fact]
    public async Task DecodeAsync_SkipsCanceledWorkWhileItIsQueued()
    {
        var backendInvocationCount = 0;
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var queuedCancellation = new CancellationTokenSource())
        using (var decoder = new LiveImageDecoder(
                   NullLogger<LiveImageDecoder>.Instance,
                   (bytes, cancellationToken) =>
                   {
                       Interlocked.Increment(ref backendInvocationCount);
                       entered.Set();
                       release.WaitOne();
                       cancellationToken.ThrowIfCancellationRequested();
                       throw new InvalidDataException("Test decode completed.");
                   }))
        {
            var active = decoder.DecodeAsync(new byte[] { 1 }, CancellationToken.None);
            Assert.True(entered.WaitOne(TimeSpan.FromSeconds(5)));

            var queued = decoder.DecodeAsync(new byte[] { 2 }, queuedCancellation.Token);
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

            release.Set();
            await Assert.ThrowsAsync<InvalidDataException>(() => active);
            Assert.Equal(1, Volatile.Read(ref backendInvocationCount));
        }
    }

    [Fact]
    public void DecodeAsync_AfterDisposeIsRejected()
    {
        var decoder = new LiveImageDecoder(NullLogger<LiveImageDecoder>.Instance);
        decoder.Dispose();

        Assert.False(decoder.IsWorkerAlive);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = decoder.DecodeAsync(new byte[] { 1 }, CancellationToken.None);
        });
    }


    private static byte[] CreateBgr24Bitmap(int width, int height)
    {
        var rowLength = checked(((width * 3) + 3) / 4 * 4);
        var pixelBytes = checked(rowLength * height);
        using (var buffer = new MemoryStream(54 + pixelBytes))
        using (var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(54 + pixelBytes);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(height);
            writer.Write((short)1);
            writer.Write((short)24);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(96);
            writer.Write(96);
            writer.Write(0);
            writer.Write(0);
            writer.Write(new byte[pixelBytes]);
            writer.Flush();
            return buffer.ToArray();
        }
    }
}
