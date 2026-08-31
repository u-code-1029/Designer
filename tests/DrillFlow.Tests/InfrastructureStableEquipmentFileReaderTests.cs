using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Infrastructure.Communication.FileExchange;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureStableEquipmentFileReaderTests
{
    private readonly StableEquipmentFileReader _reader = new StableEquipmentFileReader();

    [Fact]
    public void Presence_DistinguishesExistingAndMissingPaths()
    {
        using var directory = new InfrastructureTestDirectory();
        var existing = Path.Combine(directory.Path, "response.xml");
        var missing = Path.Combine(directory.Path, "missing.xml");
        File.WriteAllText(existing, "response");

        Assert.Equal(EquipmentFilePresence.Present, _reader.GetPresence(existing));
        Assert.Equal(EquipmentFilePresence.Absent, _reader.GetPresence(missing));
    }

    [Fact]
    public async Task TryRead_ReturnsExactBytesForClosedUnchangedFile()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "response.xml");
        var expected = Encoding.UTF8.GetBytes("<response>complete</response>");
        File.WriteAllBytes(path, expected);

        var actual = await _reader.TryReadAsync(
            path,
            TimeSpan.FromMilliseconds(10),
            maximumPayloadBytes: 1024,
            CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task TryRead_ReturnsUnavailableWhileWriterRemainsOpen()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "response.xml");
        var payload = Encoding.UTF8.GetBytes("<response>in progress</response>");
        using var writer = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        await writer.WriteAsync(payload, 0, payload.Length);
        await writer.FlushAsync();

        var actual = await _reader.TryReadAsync(
            path,
            TimeSpan.FromMilliseconds(10),
            maximumPayloadBytes: 1024,
            CancellationToken.None);

        Assert.Null(actual);
    }

    [Fact]
    public async Task TryRead_RejectsFileLargerThanConfiguredLimitBeforeReading()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "response.xml");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        var actual = await _reader.TryReadAsync(
            path,
            TimeSpan.FromMilliseconds(10),
            maximumPayloadBytes: 3,
            CancellationToken.None);

        Assert.Null(actual);
    }

    [Fact]
    public async Task TryRead_RejectsFileChangedDuringStabilityDelay()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "response.xml");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var read = _reader.TryReadAsync(
            path,
            TimeSpan.FromMilliseconds(100),
            maximumPayloadBytes: 1024,
            CancellationToken.None);
        File.WriteAllBytes(path, new byte[] { 4, 5, 6, 7 });

        Assert.Null(await read);
    }

    [Fact]
    public async Task TryRead_ObservesCancellationDuringStabilityDelay()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "response.xml");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        using var cancellation = new CancellationTokenSource();

        var read = _reader.TryReadAsync(
            path,
            TimeSpan.FromSeconds(5),
            maximumPayloadBytes: 1024,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
