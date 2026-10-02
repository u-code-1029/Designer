using System;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopCommunicationSettingsImageDefaultsTests
{
    [Fact]
    public void UnconfiguredImageFolder_RemainsBlankThroughSettingsRoundTrip()
    {
        var options = new EquipmentCommunicationOptions { ExchangeDirectory = @"\\equipment\share\old" };
        var settings = CommunicationSettings.FromOptions(options);
        var saved = JObject.FromObject(settings).ToObject<CommunicationSettings>()!;

        Assert.Empty(settings.LiveImageFolder);
        Assert.Empty(saved.LiveImageFolder);
        saved.ExchangeFolder = @"\\equipment\share\new";
        saved.ApplyTo(options);

        Assert.False(options.HasConfiguredLiveImageDirectory);
        Assert.Equal(@"\\equipment\share\new\.drillflow-live", options.LiveImageDirectory);
        options.ExchangeDirectory = @"\\equipment\share\latest";
        Assert.Equal(@"\\equipment\share\latest\.drillflow-live", options.LiveImageDirectory);
    }

    [Fact]
    public void ExplicitImageFolderMatchingTheFallback_RemainsIndependentAfterExchangeChanges()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"D:\Exchange",
            LiveImageDirectory = @"D:\Exchange\.drillflow-live"
        };
        var settings = CommunicationSettings.FromOptions(options);

        settings.ExchangeFolder = @"\\equipment\share\exchange";
        settings.ApplyTo(options);

        Assert.True(options.HasConfiguredLiveImageDirectory);
        Assert.Equal(@"D:\Exchange\.drillflow-live", settings.LiveImageFolder);
        Assert.Equal(@"D:\Exchange\.drillflow-live", options.LiveImageDirectory);
    }

    [Fact]
    public void LegacySettingsWithoutImageFolder_UseThePersistedSharedExchange()
    {
        var settings = JObject.Parse("{\"ExchangeFolder\":\"//equipment/share/exchange\"}")
            .ToObject<CommunicationSettings>()!;
        var options = new EquipmentCommunicationOptions();
        settings.ApplyTo(options);

        Assert.False(options.HasConfiguredLiveImageDirectory);
        Assert.Equal(@"\\equipment\share\exchange\.drillflow-live", options.LiveImageDirectory);
        Assert.Empty(CommunicationSettings.FromOptions(options).LiveImageFolder);
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Integration, "integration.bmp")]
    [InlineData(WorkflowNodeKind.Live, "live.bmp")]
    [InlineData(WorkflowNodeKind.Om, "om.bmp")]
    public void SavedImageFolderChanges_AffectNewActionsAndPreserveAuthoredPaths(
        WorkflowNodeKind kind, string fileName)
    {
        var settings = new CommunicationSettings
        {
            ExchangeFolder = @"C:\Exchange",
            LiveImageFolder = "//equipment/shared/old"
        };
        var activeOptions = new EquipmentCommunicationOptions();
        settings.ApplyTo(activeOptions);
        var original = WorkflowNodeFactory.Create(kind, Array.Empty<string>(), activeOptions);

        settings.LiveImageFolder = "//equipment/shared/new";
        var saved = JObject.FromObject(settings).ToObject<CommunicationSettings>()!;
        saved.ApplyTo(activeOptions);
        var next = WorkflowNodeFactory.Create(kind, new[] { original.Key }, activeOptions);

        Assert.Equal(@"\\equipment\shared\old\" + fileName,
            original.GetParameterBindings()["image_path"].RawText);
        Assert.Equal(@"\\equipment\shared\new\" + fileName,
            next.GetParameterBindings()["image_path"].RawText);
    }
}
