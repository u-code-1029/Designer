using DrillFlow.Desktop.Models;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopSettingsPageViewModelTests
{
    [Fact]
    public void BlankLiveImageFolder_RemainsAFallbackWhenExchangeFolderChanges()
    {
        using var context = new DesktopDocumentTestContext();
        var settings = context.CreateSettings();

        Assert.Empty(settings.LiveImageFolder);
        settings.ExchangeFolder = @"C:\NewExchange";
        settings.SaveCommand.Execute(null);

        Assert.False(settings.StatusIsError);
        Assert.NotNull(context.Settings.SavedPreferences);
        Assert.Empty(context.Settings.SavedPreferences!.Communication.LiveImageFolder);
        Assert.Equal(@"C:\NewExchange\.drillflow-live", context.CommunicationOptions.LiveImageDirectory);
        Assert.True(settings.OpenLiveImageFolderCommand.CanExecute(null));
        settings.OpenLiveImageFolderCommand.Execute(null);
        Assert.Equal(@"C:\NewExchange\.drillflow-live", context.LastOpenedFolder);
    }

    [Fact]
    public void ExplicitLiveImageFolder_RemainsIndependentOfExchangeFolderChanges()
    {
        using var context = new DesktopDocumentTestContext();
        context.Settings.Preferences.Communication.LiveImageFolder = @"\\camera\frames\live";
        var settings = context.CreateSettings();
        settings.ExchangeFolder = @"C:\NewExchange";

        settings.SaveCommand.Execute(null);

        Assert.False(settings.StatusIsError);
        Assert.Equal(@"\\camera\frames\live", context.CommunicationOptions.LiveImageDirectory);
        Assert.Equal(
            @"\\camera\frames\live",
            context.Settings.SavedPreferences!.Communication.LiveImageFolder);
    }

    [Fact]
    public void RelativeLiveImageFolder_IsRejectedWithoutChangingActiveOptions()
    {
        using var context = new DesktopDocumentTestContext();
        var settings = context.CreateSettings();
        var activeDirectory = context.CommunicationOptions.LiveImageDirectory;
        settings.LiveImageFolder = "relative-folder";

        settings.SaveCommand.Execute(null);

        Assert.True(settings.StatusIsError);
        Assert.Null(context.Settings.SavedPreferences);
        Assert.Equal(activeDirectory, context.CommunicationOptions.LiveImageDirectory);
    }

    [Theory]
    [InlineData(@"C:\Exchange\wild*folder")]
    [InlineData(@"C:\Exchange\bad:folder")]
    [InlineData(@"Å:\Exchange")]
    [InlineData(@"\\camera\share\wild?folder")]
    public void InvalidPersistedDirectory_DoesNotReplaceValidatedStartupOptions(string directory)
    {
        using var context = new DesktopDocumentTestContext();
        context.Settings.Preferences.Communication.ExchangeFolder = directory;
        var activeDirectory = context.CommunicationOptions.ExchangeDirectory;

        var settings = context.CreateSettings();
        settings.SaveCommand.Execute(null);

        Assert.True(settings.StatusIsError);
        Assert.NotEmpty(settings.ValidationMessage);
        Assert.Equal(activeDirectory, context.CommunicationOptions.ExchangeDirectory);
        Assert.Null(context.Settings.SavedPreferences);
    }

    [Fact]
    public void FilenameEndingInDot_IsRejectedBySharedOptionsValidation()
    {
        using var context = new DesktopDocumentTestContext();
        var settings = context.CreateSettings();
        settings.RequestFileName = "request.";

        settings.SaveCommand.Execute(null);

        Assert.True(settings.StatusIsError);
        Assert.Equal("request.xml", context.CommunicationOptions.RequestFileName);
        Assert.Null(context.Settings.SavedPreferences);
    }
}
