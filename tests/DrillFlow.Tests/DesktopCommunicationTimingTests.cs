using System;
using DrillFlow.Application.Communication;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.ViewModels;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopCommunicationTimingTests
{
    [Theory]
    [InlineData(30000, "30")]
    [InlineData(50, "0.05")]
    [InlineData(100, "0.1")]
    [InlineData(1, "0.001")]
    public void Milliseconds_AreFormattedAsInvariantDecimalSeconds(
        int milliseconds,
        string expected)
    {
        Assert.Equal(
            expected,
            SettingsPageViewModel.FormatMillisecondsAsSeconds(milliseconds));
    }

    [Theory]
    [InlineData("30", false, 30000)]
    [InlineData("0.05", false, 50)]
    [InlineData("1E-3", false, 1)]
    [InlineData("0.1", true, 100)]
    [InlineData("0", true, 0)]
    [InlineData("0.0005", true, 1)]
    [InlineData("2147483.647", true, int.MaxValue)]
    public void ValidDecimalSeconds_AreConvertedToMillisecondSettings(
        string value,
        bool allowZero,
        int expected)
    {
        var converted = SettingsPageViewModel.TryConvertSecondsToMilliseconds(
            value,
            allowZero,
            out var milliseconds);

        Assert.True(converted);
        Assert.Equal(expected, milliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    [InlineData("0.0004")]
    [InlineData("2147483.648")]
    public void InvalidOrUnrepresentableSeconds_AreRejected(string value)
    {
        Assert.False(SettingsPageViewModel.TryConvertSecondsToMilliseconds(
            value,
            allowZero: true,
            out _));
    }

    [Fact]
    public void PositiveTiming_RejectsZero()
    {
        Assert.False(SettingsPageViewModel.TryConvertSecondsToMilliseconds(
            "0",
            allowZero: false,
            out _));
    }

    [Fact]
    public void LegacyPersistedMilliseconds_KeepCustomValuesAndGainDefaultPublishDelay()
    {
        var persisted = JObject.Parse(
                "{\"ResponseTimeoutMilliseconds\":12500,"
                + "\"PollingIntervalMilliseconds\":250}")
            .ToObject<CommunicationSettings>();
        Assert.NotNull(persisted);
        Assert.Equal(12.5d, persisted!.ResponseTimeoutSeconds);
        Assert.Equal(0.25d, persisted.PollingIntervalSeconds);
        Assert.Equal(0.1d, persisted.RequestPublishDelaySeconds);

        var options = new EquipmentCommunicationOptions();
        persisted.ApplyTo(options);

        Assert.Equal(TimeSpan.FromSeconds(12.5), options.ResponseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(0.25), options.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(0.1), options.RequestPublishDelay);
        var serialized = JObject.FromObject(persisted);
        Assert.Equal(0.1d, (double)serialized[
            nameof(CommunicationSettings.RequestPublishDelaySeconds)]!);
        Assert.Null(serialized["ResponseTimeoutMilliseconds"]);
        Assert.Null(serialized["PollingIntervalMilliseconds"]);
        Assert.Null(serialized["RequestPublishDelayMilliseconds"]);
    }

    [Fact]
    public void LegacyRetryMilliseconds_MigrateToSecondsWithoutBeingWrittenAgain()
    {
        var persisted = JObject.Parse(
                "{\"RetryIntervalMilliseconds\":1250}")
            .ToObject<CommunicationSettings>();

        Assert.NotNull(persisted);
        Assert.Equal(1.25d, persisted!.RetryDelaySeconds);

        var serialized = JObject.FromObject(persisted);
        Assert.Equal(1.25d, (double)serialized[nameof(CommunicationSettings.RetryDelaySeconds)]!);
        Assert.Null(serialized["RetryIntervalMilliseconds"]);
    }

    [Fact]
    public void RuntimeEquipmentOptions_RoundTripToEditableCommunicationSettings()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            LiveImageDirectory = @"\\controller\images\live",
            RequestFileName = "in.xml",
            ResponseFileName = "out.xml",
            ResponseTimeout = TimeSpan.FromSeconds(12.5d),
            RetryEnabled = true,
            MaximumRetryCount = 3,
            RetryDelay = TimeSpan.FromSeconds(0.75d),
            PollingInterval = TimeSpan.FromSeconds(0.125d),
            RequestPublishDelay = TimeSpan.FromSeconds(0.2d),
            StableReadDelay = TimeSpan.FromSeconds(0.08d)
        };

        var editable = CommunicationSettings.FromOptions(options);
        var roundTripped = new EquipmentCommunicationOptions();
        editable.ApplyTo(roundTripped);

        Assert.Equal(options.ExchangeDirectory, roundTripped.ExchangeDirectory);
        Assert.Equal(options.LiveImageDirectory, roundTripped.LiveImageDirectory);
        Assert.Equal(options.RequestFileName, roundTripped.RequestFileName);
        Assert.Equal(options.ResponseFileName, roundTripped.ResponseFileName);
        Assert.Equal(options.ResponseTimeout, roundTripped.ResponseTimeout);
        Assert.Equal(options.RetryDelay, roundTripped.RetryDelay);
        Assert.Equal(options.StableReadDelay, roundTripped.StableReadDelay);
    }

    [Fact]
    public void LegacySettingsWithoutLiveImageFolder_FallBackToPersistedExchangeFolder()
    {
        var persisted = JObject.Parse(
                "{\"ExchangeFolder\":\"D:\\\\Shared\\\\Exchange\"}")
            .ToObject<CommunicationSettings>();
        Assert.NotNull(persisted);

        var options = new EquipmentCommunicationOptions();
        persisted!.ApplyTo(options);

        Assert.Equal(
            @"D:\Shared\Exchange\.drillflow-live",
            options.LiveImageDirectory);
        Assert.Equal(
            @"D:\Shared\Exchange\.drillflow-live",
            persisted.ResolveLiveImageFolder());
    }

    [Fact]
    public void ExplicitLiveImageFolder_IsClonedPersistedAndAppliedWithNormalizedSeparators()
    {
        var settings = new CommunicationSettings
        {
            ExchangeFolder = @"C:\Exchange",
            LiveImageFolder = "//camera/share/frames",
        };

        var clone = settings.Clone();
        var roundTripped = JObject.FromObject(clone).ToObject<CommunicationSettings>();
        Assert.NotNull(roundTripped);
        var options = new EquipmentCommunicationOptions();
        roundTripped!.ApplyTo(options);

        Assert.Equal("//camera/share/frames", clone.LiveImageFolder);
        Assert.Equal(@"\\camera\share\frames", options.LiveImageDirectory);
        Assert.Equal(@"\\camera\share\frames", roundTripped.ResolveLiveImageFolder());
    }
}
