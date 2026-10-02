using System;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopWorkflowNodeFactoryTests
{
    [Theory]
    [InlineData(WorkflowNodeKind.Integration, "integration.bmp")]
    [InlineData(WorkflowNodeKind.Live, "live.bmp")]
    [InlineData(WorkflowNodeKind.Om, "om.bmp")]
    public void ImageActions_UseConfiguredSharedDirectory(
        WorkflowNodeKind kind,
        string fileName)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            LiveImageDirectory = @"\\equipment\shared\images\"
        };

        var action = WorkflowNodeFactory.Create(kind, Array.Empty<string>(), options);

        Assert.Equal(@"\\equipment\shared\images\" + fileName, ImagePath(action));
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Integration, "integration.bmp")]
    [InlineData(WorkflowNodeKind.Live, "live.bmp")]
    [InlineData(WorkflowNodeKind.Om, "om.bmp")]
    public void ImageActions_NormalizeConfiguredForwardSlashesForWindowsMessages(
        WorkflowNodeKind kind,
        string fileName)
    {
        var options = new EquipmentCommunicationOptions
        {
            LiveImageDirectory = " //equipment/shared/images/ "
        };

        var action = WorkflowNodeFactory.Create(kind, Array.Empty<string>(), options);

        Assert.Equal(@"\\equipment\shared\images\" + fileName, ImagePath(action));
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Integration, "integration.bmp", null)]
    [InlineData(WorkflowNodeKind.Live, "live.bmp", "")]
    [InlineData(WorkflowNodeKind.Om, "om.bmp", "   ")]
    public void MissingImageDirectory_UsesExchangeFolderFallback(
        WorkflowNodeKind kind,
        string fileName,
        string? configuredDirectory)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"\\equipment\shared\exchange",
            LiveImageDirectory = configuredDirectory!
        };

        var action = WorkflowNodeFactory.Create(kind, Array.Empty<string>(), options);

        Assert.Equal(
            @"\\equipment\shared\exchange\.drillflow-live\" + fileName,
            ImagePath(action));
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Integration, "integration.bmp")]
    [InlineData(WorkflowNodeKind.Live, "live.bmp")]
    [InlineData(WorkflowNodeKind.Om, "om.bmp")]
    public void ChangedSettings_ApplyToNewActionsAndPreserveExistingBindings(
        WorkflowNodeKind kind,
        string fileName)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            LiveImageDirectory = @"\\equipment\shared\old"
        };
        var original = WorkflowNodeFactory.Create(kind, Array.Empty<string>(), options);

        options.LiveImageDirectory = @"\\equipment\shared\new";
        var next = WorkflowNodeFactory.Create(kind, new[] { original.Key }, options);
        options.LiveImageDirectory = string.Empty;
        options.ExchangeDirectory = @"D:\SharedExchange";
        var fallback = WorkflowNodeFactory.Create(kind, new[] { original.Key, next.Key }, options);

        Assert.Equal(@"\\equipment\shared\old\" + fileName, ImagePath(original));
        Assert.Equal(@"\\equipment\shared\new\" + fileName, ImagePath(next));
        Assert.Equal(@"D:\SharedExchange\.drillflow-live\" + fileName, ImagePath(fallback));
        Assert.NotEqual(original.Key, next.Key);
        Assert.NotEqual(next.Key, fallback.Key);
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Integration, "integration.bmp")]
    [InlineData(WorkflowNodeKind.Live, "live.bmp")]
    [InlineData(WorkflowNodeKind.Om, "om.bmp")]
    public void LocalDriveRoot_UsesOneDirectorySeparator(
        WorkflowNodeKind kind,
        string fileName)
    {
        var options = new EquipmentCommunicationOptions { LiveImageDirectory = @"D:\" };

        var action = WorkflowNodeFactory.Create(kind, Array.Empty<string>(), options);

        Assert.Equal(@"D:\" + fileName, ImagePath(action));
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Integration)]
    [InlineData(WorkflowNodeKind.Live)]
    [InlineData(WorkflowNodeKind.Om)]
    public void NoConfiguredOrFallbackDirectory_RejectsImageCreation(WorkflowNodeKind kind)
    {
        Assert.Throws<InvalidOperationException>(() => WorkflowNodeFactory.Create(
            kind,
            Array.Empty<string>(),
            new EquipmentCommunicationOptions()));
    }

    [Fact]
    public void NonImageAction_StillSupportsUnconfiguredImageDirectoryAndUniqueAliases()
    {
        var action = Assert.IsType<DelayNode>(WorkflowNodeFactory.Create(
            WorkflowNodeKind.Delay,
            new[] { "DELAY_1", "delay_2" },
            new EquipmentCommunicationOptions()));

        Assert.Equal("delay_3", action.Key);
        Assert.Equal("delay_3", action.DisplayName);
        Assert.Equal("1000", action.DurationMilliseconds.RawText);
    }

    private static string ImagePath(WorkflowNode action) =>
        action.GetParameterBindings()["image_path"].RawText;
}
