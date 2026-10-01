using DrillFlow.Application.RealtimeVideo;
using DrillFlow.Desktop.ViewModels;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopRealtimeVideoSettingsViewModelTests
{
    [Theory]
    [InlineData("https://equipment.example/live", "https://equipment.example/Live")]
    [InlineData("https://equipment.example/live?camera=A", "https://equipment.example/live?camera=a")]
    public void EndpointCaseChange_RequiresRestartForCaseSensitivePathsAndQueries(
        string originalEndpoint,
        string changedEndpoint)
    {
        var original = new RealtimeVideoOptions();
        original.SignalR.HubEndpoint = originalEndpoint;
        var changed = original.Clone();
        changed.SignalR.HubEndpoint = changedEndpoint;

        Assert.False(RealtimeVideoSettingsViewModel.AreEquivalent(original, changed));
        Assert.True(RealtimeVideoSettingsViewModel.AreEquivalent(original, original.Clone()));
    }
}
