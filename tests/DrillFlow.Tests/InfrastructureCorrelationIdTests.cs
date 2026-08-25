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
    public async Task Provider_IsMonotonicAcrossInstancesAndConcurrentAllocations()
    {
        using var directory = new InfrastructureTestDirectory();
        var options = Options.Create(new CorrelationIdStoreOptions
        {
            StateFilePath = Path.Combine(directory.Path, "correlation.txt"),
        });

        using var first = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);
        using var second = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);

        var allocations = new List<Task<int>>();
        for (var index = 0; index < 40; index++)
        {
            allocations.Add((index & 1) == 0
                ? first.NextAsync(CancellationToken.None)
                : second.NextAsync(CancellationToken.None));
        }

        var ids = await Task.WhenAll(allocations);
        Assert.Equal(Enumerable.Range(1, 40), ids.OrderBy(value => value));

        using var afterRestart = new PersistentCorrelationIdProvider(
            options,
            NullLogger<PersistentCorrelationIdProvider>.Instance);
        Assert.Equal(41, await afterRestart.NextAsync(CancellationToken.None));
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

