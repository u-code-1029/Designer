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
        Assert.Equal(
            Path.Combine(options.ExchangeDirectory, ".drillflow-live"),
            options.LiveImageDirectory);
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

    [Theory]
    [InlineData("C:/Shared/LiveFrames", @"C:\Shared\LiveFrames")]
    [InlineData("//server/share/LiveFrames", @"\\server\share\LiveFrames")]
    public void EquipmentOptions_NormalizeConfiguredLiveImageDirectory(
        string configured,
        string expected)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            LiveImageDirectory = configured,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Failures ?? Array.Empty<string>()));
        Assert.Equal(expected, options.LiveImageDirectory);
    }

    [Theory]
    [InlineData("relative-folder")]
    [InlineData(@"C:LiveFrames")]
    [InlineData(@"\LiveFrames")]
    [InlineData(@"\\server")]
    [InlineData(@"C:\Live|Frames")]
    public void EquipmentOptions_RejectInvalidConfiguredLiveImageDirectory(string path)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            LiveImageDirectory = path,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("live", StringComparison.OrdinalIgnoreCase)
                       && failure.Contains("absolute", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void EquipmentOptions_AllowZeroRequestPublishDelay()
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = Path.GetFullPath(Path.GetTempPath()),
            RequestPublishDelay = TimeSpan.Zero,
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Failures ?? Array.Empty<string>()));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(2147483648d)]
    public void EquipmentOptions_RejectRequestPublishDelayOutsideSupportedRange(
        double milliseconds)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = Path.GetFullPath(Path.GetTempPath()),
            RequestPublishDelay = TimeSpan.FromMilliseconds(milliseconds),
        };

        var result = new EquipmentCommunicationOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                nameof(EquipmentCommunicationOptions.RequestPublishDelay),
                StringComparison.Ordinal));
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
    public void EquipmentOptions_RejectNamesThatNormalizeToAnotherExchangeFileOrSidecar()
    {
        var validator = new EquipmentCommunicationOptionsValidator();
        foreach (var responseName in new[] { "request.xml ", "request.xml.", ".drillflow.exchange.lock " })
        {
            var options = new EquipmentCommunicationOptions
            {
                ExchangeDirectory = @"C:\Exchange",
                RequestFileName = "request.xml",
                ResponseFileName = responseName
            };

            Assert.True(validator.Validate(null, options).Failed, responseName);
        }
    }

    [Theory]
    [InlineData("CON.xml")]
    [InlineData("prn.response.xml")]
    [InlineData("AUX.xml")]
    [InlineData("nul.xml")]
    [InlineData("COM1.xml")]
    [InlineData("Lpt9.xml")]
    [InlineData("NUL .xml")]
    [InlineData("COM¹.xml")]
    [InlineData("LPT².xml")]
    public void EquipmentOptions_RejectReservedWindowsDeviceFileNames(string fileName)
    {
        var validator = new EquipmentCommunicationOptionsValidator();
        foreach (var useAsRequest in new[] { true, false })
        {
            var options = new EquipmentCommunicationOptions
            {
                ExchangeDirectory = @"C:\Exchange",
                RequestFileName = useAsRequest ? fileName : "request.xml",
                ResponseFileName = useAsRequest ? "response.xml" : fileName
            };

            var result = validator.Validate(null, options);

            Assert.True(result.Failed);
            Assert.Contains(result.Failures, failure => failure.Contains("device", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("COM10.xml")]
    [InlineData("NUL-result.xml")]
    [InlineData("result version.xml")]
    public void EquipmentOptions_AcceptOrdinaryNamesNearReservedDeviceNames(string fileName)
    {
        var options = new EquipmentCommunicationOptions
        {
            ExchangeDirectory = @"C:\Exchange",
            RequestFileName = fileName
        };

        Assert.True(new EquipmentCommunicationOptionsValidator().Validate(null, options).Succeeded);
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
