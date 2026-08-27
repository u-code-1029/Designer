using System;
using System.Collections.Generic;
using DrillFlow.Application.Communication;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationEquipmentResponseMessageTests
{
    [Fact]
    public void Constructor_ExposesValidatedCoordinatesAndAbsoluteImagePaths()
    {
        var local = Create(@"C:\images\result.png");
        var unc = Create(@"\\server\share\result.png");

        Assert.Equal(0.125d, local.StageX);
        Assert.Equal(-0.25d, local.StageY);
        Assert.Equal(@"C:\images\result.png", local.ImagePath);
        Assert.Equal(@"\\server\share\result.png", unc.ImagePath);
    }

    [Fact]
    public void Constructor_RequiresPositiveIndexAndExactReturnCommand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EquipmentResponseMessage(0, "return", ValidProperties()));
        Assert.Throws<ArgumentException>(() =>
            new EquipmentResponseMessage(1, "Return", ValidProperties()));
        Assert.Throws<ArgumentException>(() =>
            new EquipmentResponseMessage(1, null!, ValidProperties()));
    }

    [Fact]
    public void Constructor_RequiresFiniteCanonicalStageCoordinates()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new EquipmentResponseMessage(
                1,
                "return",
                new Dictionary<string, object?> { ["stage_x"] = 0d }));
        Assert.Throws<InvalidOperationException>(() =>
            new EquipmentResponseMessage(
                1,
                "return",
                new Dictionary<string, object?>
                {
                    ["stage_x"] = double.PositiveInfinity,
                    ["stage_y"] = 0d
                }));
        Assert.Throws<InvalidOperationException>(() =>
            new EquipmentResponseMessage(
                1,
                "return",
                new Dictionary<string, object?>
                {
                    ["stage_x"] = "0",
                    ["stage_y"] = 0d
                }));
    }

    [Fact]
    public void Constructor_RejectsCaseInsensitiveDuplicatesAndRuntimeMetadataCollisions()
    {
        var duplicateStage = ValidProperties();
        duplicateStage["STAGE_X"] = 1d;
        Assert.Throws<ArgumentException>(() =>
            new EquipmentResponseMessage(1, "return", duplicateStage));

        var duplicateExtension = ValidProperties();
        duplicateExtension["Trace"] = 1;
        duplicateExtension["trace"] = 2;
        Assert.Throws<ArgumentException>(() =>
            new EquipmentResponseMessage(1, "return", duplicateExtension));

        foreach (var reservedName in new[] { "Index", "COMMAND", "iteration_path" })
        {
            var properties = ValidProperties();
            properties[reservedName] = 7;
            Assert.Throws<ArgumentException>(() =>
                new EquipmentResponseMessage(1, "return", properties));
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
                     @"C:\images\bad?.png",
                     "C:\\images\\bad\0.png"
                 })
        {
            var exception = Record.Exception(() => Create(invalidPath));
            Assert.IsType<InvalidOperationException>(exception);
        }
    }

    private static EquipmentResponseMessage Create(string? imagePath = null)
    {
        var properties = ValidProperties();
        if (imagePath != null)
        {
            properties["image_path"] = imagePath;
        }

        return new EquipmentResponseMessage(1, "return", properties);
    }

    private static Dictionary<string, object?> ValidProperties()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stage_x"] = 0.125d,
            ["stage_y"] = -0.25d
        };
    }
}
