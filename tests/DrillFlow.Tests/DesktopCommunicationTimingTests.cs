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
        Assert.Equal(12500, persisted!.ResponseTimeoutMilliseconds);
        Assert.Equal(250, persisted.PollingIntervalMilliseconds);
        Assert.Equal(100, persisted.RequestPublishDelayMilliseconds);

        var options = new EquipmentCommunicationOptions();
        persisted.ApplyTo(options);

        Assert.Equal(TimeSpan.FromSeconds(12.5), options.ResponseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(0.25), options.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(0.1), options.RequestPublishDelay);
        Assert.Equal(100, (int)JObject.FromObject(persisted)[
            nameof(CommunicationSettings.RequestPublishDelayMilliseconds)]!);
    }
}
