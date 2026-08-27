using System;
using System.IO;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.Communication;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationRequestLifecycleOptionsTests
{
    [Fact]
    public void EquipmentOptions_DefaultToBestEffortRequestCleanupAfterResponse()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = Path.GetFullPath(Path.GetTempPath()),
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Failures ?? Array.Empty<string>()));
        Assert.Equal(
            ApplicationRequestFileLifecycle.DeleteAfterResponse,
            options.ApplicationRequestLifecycle);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.PollingInterval);
    }

    [Fact]
    public void EquipmentOptions_RejectUnsupportedApplicationRequestLifecycle()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = Path.GetFullPath(Path.GetTempPath()),
            ApplicationRequestLifecycle = (ApplicationRequestFileLifecycle)int.MaxValue,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                nameof(EquipmentCommunicationOptions.ApplicationRequestLifecycle),
                StringComparison.Ordinal));
    }
}
