using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Desktop.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopLiveCaptureSnapshotStoreTests
{
    [Fact]
    public async Task AcquireAsync_PreservesCapturedBytes_WhenEquipmentSourceIsReplaced()
    {
        var root = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(root, "equipment-capture.png");
        var snapshotsPath = Path.Combine(root, "snapshots");
        var original = Encoding.UTF8.GetBytes("first equipment image bytes");
        var replacement = Encoding.UTF8.GetBytes("replacement image bytes");
        File.WriteAllBytes(sourcePath, original);

        try
        {
            using (var store = new LiveCaptureSnapshotStore(
                       NullLogger<LiveCaptureSnapshotStore>.Instance,
                       snapshotsPath))
            using (var snapshot = await store.AcquireAsync(sourcePath, CancellationToken.None))
            {
                File.WriteAllBytes(sourcePath, replacement);

                Assert.Equal(original, File.ReadAllBytes(snapshot.Path));
            }

            Assert.False(Directory.Exists(snapshotsPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 99, false)]
    [InlineData(100, 101, false)]
    [InlineData(0, 0, false)]
    public void IsConsistentSnapshot_RequiresPositiveUnchangedAndFullyCopiedLength(
        long sourceLength,
        long copiedLength,
        bool expected)
    {
        var writeTime = new DateTime(2026, 8, 27, 1, 2, 3, DateTimeKind.Utc);

        var result = LiveCaptureSnapshotStore.IsConsistentSnapshot(
            sourceLength,
            writeTime,
            sourceLength,
            writeTime,
            copiedLength);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsConsistentSnapshot_RejectsChangedSourceMetadata()
    {
        var writeTime = new DateTime(2026, 8, 27, 1, 2, 3, DateTimeKind.Utc);

        Assert.False(LiveCaptureSnapshotStore.IsConsistentSnapshot(
            100,
            writeTime,
            101,
            writeTime,
            100));
        Assert.False(LiveCaptureSnapshotStore.IsConsistentSnapshot(
            100,
            writeTime,
            100,
            writeTime.AddSeconds(1),
            100));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "DrillFlow.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup only.
        }
    }
}
