using System;
using System.Diagnostics;
using DrillFlow.Desktop.Services;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopDefaultFileLauncherTests
{
    [Fact]
    public void Open_UsesWindowsShellAssociationWithoutAnExistenceProbe()
    {
        ProcessStartInfo? observed = null;
        var launcher = new DefaultFileLauncher(startInfo => observed = startInfo);

        var openedPath = launcher.Open(@"  C:\captures\camera image.png  ");

        Assert.Equal(@"C:\captures\camera image.png", openedPath);
        Assert.NotNull(observed);
        Assert.Equal(openedPath, observed!.FileName);
        Assert.True(observed.UseShellExecute);
        Assert.Empty(observed.Arguments);
    }

    [Fact]
    public void Open_AllowsRootedUncPathWithoutTouchingTheShare()
    {
        ProcessStartInfo? observed = null;
        var launcher = new DefaultFileLauncher(startInfo => observed = startInfo);

        var openedPath = launcher.Open(@"\\offline-host\capture\image.png");

        Assert.Equal(@"\\offline-host\capture\image.png", openedPath);
        Assert.Equal(openedPath, observed!.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("capture.png")]
    public void Open_RejectsMissingOrRelativePathBeforeLaunching(string path)
    {
        var launchCount = 0;
        var launcher = new DefaultFileLauncher(_ => launchCount++);

        Assert.Throws<ArgumentException>(() => launcher.Open(path));
        Assert.Equal(0, launchCount);
    }
}
