using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Persistence;
using DrillFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureCorrelationIdTests
{
    [Fact]
    public async Task Provider_ReservesAHighWaterBlockAndConsumesItInMemory()
    {
        using var directory = new InfrastructureTestDirectory();
        var statePath = Path.Combine(directory.Path, "correlation.txt");
        var options = Options.Create(new CorrelationIdStoreOptions
        {
            StateFilePath = statePath,
        });

        using var provider = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);

        Assert.Equal(1, await provider.NextAsync(CancellationToken.None));
        Assert.Equal("256", File.ReadAllText(statePath));

        for (var expected = 2; expected <= 256; expected++)
        {
            Assert.Equal(expected, await provider.NextAsync(CancellationToken.None));
        }

        Assert.Equal("256", File.ReadAllText(statePath));
        Assert.Equal(257, await provider.NextAsync(CancellationToken.None));
        Assert.Equal("512", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task Provider_UsesDisjointBlocksAcrossInstancesAndSkipsUnusedIdsAfterRestart()
    {
        using var directory = new InfrastructureTestDirectory();
        var statePath = Path.Combine(directory.Path, "correlation.txt");
        var options = Options.Create(new CorrelationIdStoreOptions
        {
            StateFilePath = statePath,
        });

        using var first = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);
        using var second = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);

        Assert.Equal(1, await first.NextAsync(CancellationToken.None));
        Assert.Equal(257, await second.NextAsync(CancellationToken.None));

        var allocations = new List<Task<int>>();
        for (var index = 0; index < 40; index++)
        {
            allocations.Add((index & 1) == 0
                ? first.NextAsync(CancellationToken.None)
                : second.NextAsync(CancellationToken.None));
        }

        var ids = await Task.WhenAll(allocations);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(1, ids);
        Assert.DoesNotContain(257, ids);
        Assert.Equal("512", File.ReadAllText(statePath));

        using var afterRestart = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);
        Assert.Equal(513, await afterRestart.NextAsync(CancellationToken.None));
        Assert.Equal("768", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task Provider_DoesNotSilentlyResetCorruptState()
    {
        using var directory = new InfrastructureTestDirectory();
        var statePath = Path.Combine(directory.Path, "correlation.txt");
        File.WriteAllText(statePath, "not-an-id");
        using var provider = new PersistentCorrelationIdProvider(
            Options.Create(new CorrelationIdStoreOptions { StateFilePath = statePath }),
            NullLogger<PersistentCorrelationIdProvider>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.NextAsync(CancellationToken.None));

        Assert.Equal("not-an-id", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task Provider_UsesTheRemainingPositiveInt32IdsBeforeReportingExhaustion()
    {
        using var directory = new InfrastructureTestDirectory();
        var statePath = Path.Combine(directory.Path, "correlation.txt");
        File.WriteAllText(
            statePath,
            (int.MaxValue - 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var options = Options.Create(new CorrelationIdStoreOptions { StateFilePath = statePath });

        using (var provider = new PersistentCorrelationIdProvider(
                   options,
                   NullLogger<PersistentCorrelationIdProvider>.Instance))
        {
            Assert.Equal(int.MaxValue - 1, await provider.NextAsync(CancellationToken.None));
            Assert.Equal(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                File.ReadAllText(statePath));
            Assert.Equal(int.MaxValue, await provider.NextAsync(CancellationToken.None));
            await Assert.ThrowsAsync<OverflowException>(
                () => provider.NextAsync(CancellationToken.None));
        }

        using var afterRestart = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);
        await Assert.ThrowsAsync<OverflowException>(
            () => afterRestart.NextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Provider_CancellationWhileWaitingForTheProcessLockDoesNotAdvanceState()
    {
        using var directory = new InfrastructureTestDirectory();
        var statePath = Path.Combine(directory.Path, "correlation.txt");
        var lockPath = statePath + ".lock";
        var options = Options.Create(new CorrelationIdStoreOptions { StateFilePath = statePath });
        using var provider = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);

        using (var heldLock = new FileStream(
                   lockPath,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provider.NextAsync(cancellation.Token));
        }

        Assert.False(File.Exists(statePath));
        Assert.Equal(1, await provider.NextAsync(CancellationToken.None));
        Assert.Equal("256", File.ReadAllText(statePath));
    }
}

internal sealed class InfrastructureTestDirectory : IDisposable
{
    public InfrastructureTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DrillFlow.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
