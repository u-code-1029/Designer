using System;
using System.Collections.Generic;
using DrillFlow.Application.Communication;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationEquipmentResponseMessageTests
{
    [Fact]
    public void Constructor_ExposesCanonicalEnvelopeAndTypedHelpers()
    {
        var response = new EquipmentResponseMessage(
            17,
            EquipmentActionNames.Integration,
            0,
            new Dictionary<string, object?>
            {
                ["hfw"] = 3.02E-6,
                ["frame_count"] = 8,
                ["image_path"] = @"C:\images\result.png"
            });

        Assert.Equal("response", response.Type);
        Assert.Equal(17, response.CorrelationId);
        Assert.Equal("integration", response.Action);
        Assert.Equal(0, response.Result);
        Assert.True(response.IsSuccess);
        Assert.Equal(3.02E-6, response.Hfw);
        Assert.Equal(8, response.FrameCount);
        Assert.Equal(@"C:\images\result.png", response.ImagePath);
    }

    [Fact]
    public void Constructor_RequiresPositiveCorrelationKnownActionAndBinaryResult()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EquipmentResponseMessage(0, EquipmentActionNames.Abort, 0));
        Assert.Throws<ArgumentException>(() =>
            new EquipmentResponseMessage(1, "unknown", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EquipmentResponseMessage(1, EquipmentActionNames.Abort, 2));
    }

    [Fact]
    public void Constructor_RejectsCaseInsensitiveDuplicatesAndEnvelopeCollisions()
    {
        var duplicate = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Trace"] = 1,
            ["trace"] = 2
        };
        Assert.Throws<ArgumentException>(() =>
            new EquipmentResponseMessage(1, EquipmentActionNames.Abort, 0, duplicate));

        foreach (var reservedName in new[]
                 {
                     "Type", "CORRELATION_ID", "Action", "result", "iteration_path"
                 })
        {
            Assert.Throws<ArgumentException>(() =>
                new EquipmentResponseMessage(
                    1,
                    EquipmentActionNames.Abort,
                    0,
                    new Dictionary<string, object?> { [reservedName] = 7 }));
        }
    }

    [Fact]
    public void Constructor_RejectsRelativeOrMalformedImagePathsWithoutPathApiExceptions()
    {
        foreach (var invalidPath in new[]
                 {
                     "result.png",
                     @"C:result.png",
                     @"\rooted-on-current-drive.png",
                     @"\\server\share",
                     @"C:\images\.",
                     @"\\server\share\..",
                     @"C:\images\trailing.",
                     @"C:\images\bad?.png",
                     "C:\\images\\bad\0.png"
                 })
        {
            var exception = Record.Exception(() => new EquipmentResponseMessage(
                1,
                EquipmentActionNames.Live,
                0,
                new Dictionary<string, object?> { ["image_path"] = invalidPath }));
            Assert.IsType<InvalidOperationException>(exception);
        }
    }

    [Fact]
    public void ResultOne_RemainsAFirstClassResponseForTheRunnerToDecide()
    {
        var response = new EquipmentResponseMessage(
            1,
            EquipmentActionNames.Abort,
            1);

        Assert.Equal(1, response.Result);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public void LensResponse_ExposesAndEagerlyValidatesCurrentMode()
    {
        var response = new EquipmentResponseMessage(
            2,
            EquipmentActionNames.Lens,
            0,
            new Dictionary<string, object?> { ["current_lens_mode"] = "lens2" });

        Assert.Equal("lens2", response.CurrentLensMode);
        Assert.Throws<InvalidOperationException>(() => new EquipmentResponseMessage(
            3,
            EquipmentActionNames.Lens,
            0,
            new Dictionary<string, object?> { ["current_lens_mode"] = "no_change" }));
    }
}
