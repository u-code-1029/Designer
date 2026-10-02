using System;
using DrillFlow.Application.Communication;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationSharedImagePathTests
{
    [Theory]
    [InlineData(@"C:\SharedImages", "live-91.bmp", @"C:\SharedImages\live-91.bmp")]
    [InlineData(@"C:\SharedImages\", "integration-92.bmp", @"C:\SharedImages\integration-92.bmp")]
    [InlineData("  //equipment/images/  ", "om-93.bmp", @"\\equipment\images\om-93.bmp")]
    [InlineData(@"C:\", "live-94.bmp", @"C:\live-94.bmp")]
    public void Create_UsesWindowsWireSeparatorsForEveryImageAction(
        string directory,
        string fileName,
        string expected)
    {
        var path = EquipmentImagePath.Create(directory, fileName);

        Assert.Equal(expected, path);
        Assert.True(EquipmentResponseMessage.IsSupportedAbsoluteImagePath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_RejectsMissingOutputDirectory(string? directory)
    {
        var error = Assert.Throws<ArgumentException>(() => EquipmentImagePath.Create(directory, "frame.bmp"));
        Assert.Equal("directory", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\other.bmp")]
    [InlineData("../other.bmp")]
    [InlineData(@"C:\other.bmp")]
    [InlineData("other:stream.bmp")]
    [InlineData("other?.bmp")]
    [InlineData("other\u0000.bmp")]
    public void Create_RejectsNamesThatCanEscapeOrAlterTheOutputPath(string fileName)
    {
        var error = Assert.Throws<ArgumentException>(() => EquipmentImagePath.Create(@"C:\SharedImages", fileName));
        Assert.Equal("fileName", error.ParamName);
    }

    [Fact]
    public void Snapshot_PreservesSharedFolderUntilTheNextCapture()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            LiveImageDirectory = "//equipment/old-images/",
        };
        var original = EquipmentCommunicationSnapshot.Capture(options);

        options.LiveImageDirectory = @"\\equipment\new-images";
        options.ExchangeDirectory = @"D:\NewExchange";
        var next = EquipmentCommunicationSnapshot.Capture(options);

        foreach (var action in new[] { EquipmentActionNames.Live, EquipmentActionNames.Integration, EquipmentActionNames.Om })
        {
            Assert.Equal(
                @"\\equipment\old-images\" + action + "-1.bmp",
                EquipmentImagePath.Create(original.LiveImageDirectory, action + "-1.bmp"));
            Assert.Equal(
                @"\\equipment\new-images\" + action + "-2.bmp",
                EquipmentImagePath.Create(next.LiveImageDirectory, action + "-2.bmp"));
        }
    }

    [Theory]
    [InlineData("C:/Exchange/", @"C:\Exchange\.drillflow-live")]
    [InlineData("//equipment/exchange/", @"\\equipment\exchange\.drillflow-live")]
    public void Snapshot_OmittedSharedFolderUsesExchangeSubfolderWithoutPlatformSeparators(
        string exchangeDirectory,
        string expectedDirectory)
    {
        var options = new EquipmentCommunicationOptions { ExchangeDirectory = exchangeDirectory };
        var original = EquipmentCommunicationSnapshot.Capture(options);
        Assert.False(options.HasConfiguredLiveImageDirectory);

        options.ExchangeDirectory = @"D:\NextExchange";
        var next = EquipmentCommunicationSnapshot.Capture(options);

        Assert.Equal(expectedDirectory, original.LiveImageDirectory);
        Assert.Equal(@"D:\NextExchange\.drillflow-live", next.LiveImageDirectory);
        Assert.Equal(expectedDirectory + @"\integration-1.bmp", EquipmentImagePath.Create(original.LiveImageDirectory, "integration-1.bmp"));
    }

    [Fact]
    public void ExplicitFallbackFolder_RemainsExplicitWhenExchangeDirectoryChanges()
    {
        var options = new EquipmentCommunicationOptions { ExchangeDirectory = @"C:\Exchange" };
        var fallback = options.LiveImageDirectory;
        Assert.False(options.HasConfiguredLiveImageDirectory);

        options.LiveImageDirectory = fallback;
        Assert.True(options.HasConfiguredLiveImageDirectory);
        options.ExchangeDirectory = @"\\equipment\exchange";

        Assert.Equal(fallback, options.LiveImageDirectory);
        options.LiveImageDirectory = "  ";
        Assert.False(options.HasConfiguredLiveImageDirectory);
        Assert.Equal(@"\\equipment\exchange\.drillflow-live", options.LiveImageDirectory);
    }
}
