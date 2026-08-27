using System;
using System.IO;
using DrillFlow.Application.Communication;
using DrillFlow.Application.Persistence;
using DrillFlow.Infrastructure.Communication;
using DrillFlow.Infrastructure.Persistence;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureOptionsTests
{
    [Fact]
    public void EquipmentOptions_DefaultPolicyAndValidPaths_AreAccepted()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = Path.GetFullPath(Path.GetTempPath()),
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Failures ?? Array.Empty<string>()));
        Assert.Equal(ApplicationResponseFileLifecycle.DeleteAfterRead, options.ApplicationResponseLifecycle);
        Assert.False(options.RetryEnabled);
        Assert.Equal("request.xml", options.RequestFileName);
        Assert.Equal("response.xml", options.ResponseFileName);
    }

    [Theory]
    [InlineData("C:/Exchange/Frames", @"C:\Exchange\Frames")]
    [InlineData("//server/share/Exchange", @"\\server\share\Exchange")]
    public void EquipmentOptions_NormalizeAcceptedWindowsDirectorySeparators(
        string configured,
        string expected)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = configured,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, options.ExchangeDirectory);
    }

    [Fact]
    public void EquipmentOptions_RejectUnsafeNamesAndInvalidRetryConfiguration()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = "relative-folder",
            RequestFileName = "same",
            ResponseFileName = "same",
            RetryEnabled = true,
            MaximumRetryCount = 0,
            PollingInterval = TimeSpan.Zero,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed, "Expected options validation to fail.");
        Assert.Contains(result.Failures, failure => failure.Contains("absolute", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("extension", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("different", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("at least one", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"C:Exchange")]
    [InlineData(@"\Exchange")]
    [InlineData(@"//server")]
    public void EquipmentOptions_RejectDriveRelativeAndCurrentDriveRootedDirectories(string path)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = path
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("absolute", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EquipmentOptions_ReserveTheCrossProcessExchangeLockFileName(bool useAsRequest)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = Path.GetFullPath(Path.GetTempPath()),
            RequestFileName = useAsRequest
                ? EquipmentCommunicationOptions.ExchangeLockFileName
                : "request.xml",
            ResponseFileName = useAsRequest
                ? "response.xml"
                : EquipmentCommunicationOptions.ExchangeLockFileName,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("reserved", StringComparison.OrdinalIgnoreCase)
                       && failure.Contains("lock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CorrelationStoreOptions_RequireAbsoluteFilePath()
    {
        var result = new CorrelationIdStoreOptionsValidator().Validate(
            null,
            new CorrelationIdStoreOptions { StateFilePath = "state.txt" });

        Assert.True(result.Failed, "Expected options validation to fail.");
    }
}
