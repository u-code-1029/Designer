using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopResponseSimulationPreviewTests
{
    [Fact]
    public void TemporaryImage_IsFrozenMosaicAndSurvivesFileCleanupInMemory()
    {
        var directory = CreateTestDirectory();
        TemporaryResponseImage? generated = null;
        try
        {
            using (var service = new TemporaryResponseImageService(
                       NullLogger<TemporaryResponseImageService>.Instance,
                       directory))
            {
                generated = service.CreateTemporaryImage();

                Assert.Equal(TemporaryResponseImageService.ImageWidth, generated.ImageSource.PixelWidth);
                Assert.Equal(TemporaryResponseImageService.ImageHeight, generated.ImageSource.PixelHeight);
                Assert.True(generated.ImageSource.IsFrozen);
                Assert.True(File.Exists(generated.Path));

                // Two points inside one 64 px tile share a color, while the next tile is guaranteed
                // to differ. This distinguishes the requested mosaic from per-pixel random noise.
                Assert.Equal(ReadPixel(generated.ImageSource, 8, 8), ReadPixel(generated.ImageSource, 40, 40));
                Assert.NotEqual(ReadPixel(generated.ImageSource, 8, 8), ReadPixel(generated.ImageSource, 72, 8));
            }

            Assert.NotNull(generated);
            Assert.False(File.Exists(generated!.Path));
            Assert.Equal(4, ReadPixel(generated.ImageSource, 8, 8).Length);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void TemporaryImage_ExplicitReleaseDeletesOnlyServiceOwnedFile()
    {
        var directory = CreateTestDirectory();
        try
        {
            using (var service = new TemporaryResponseImageService(
                       NullLogger<TemporaryResponseImageService>.Instance,
                       directory))
            {
                var generated = service.CreateTemporaryImage();
                var unrelated = Path.Combine(directory, "controller.png");
                File.WriteAllBytes(unrelated, new byte[] { 1, 2, 3 });

                Assert.False(service.TryReleaseTemporaryImage(unrelated));
                Assert.True(File.Exists(unrelated));
                Assert.True(service.TryReleaseTemporaryImage(generated.Path));
                Assert.False(File.Exists(generated.Path));
                Assert.False(service.TryReleaseTemporaryImage(generated.Path));
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RegenerateImage_PreservesEditedPayloadAndAtomicallyReplacesPreview()
    {
        var firstImage = CreateTestBitmap(10, 20, 30);
        var secondImage = CreateTestBitmap(40, 50, 60);
        const string firstPath = @"C:\temp\first.png";
        const string secondPath = @"C:\temp\second.png";
        const string editedPayload = "{\"index\":7,\"command\":\"return\",\"stage_x\":0.125,"
                                     + "\"stage_y\":-0.25,\"custom\":\"keep\","
                                     + "\"image_path\":\"C:\\\\temp\\\\first.png\"}";
        string? payloadReceivedByRegenerator = null;

        var viewModel = new ResponseSimulationDialogViewModel(
            "Move1 (Move)",
            "JSON",
            @"C:\exchange\response.json",
            "Index 7, command: move",
            editedPayload,
            new ResponseSimulationPreview(firstImage, firstPath, editedPayload),
            payload =>
            {
                payloadReceivedByRegenerator = payload;
                return Task.FromResult<ResponseSimulationPreview?>(
                    new ResponseSimulationPreview(
                        secondImage,
                        secondPath,
                        ResponseSimulationDialogService.SynchronizeJsonImagePath(payload, secondPath)));
            },
            "Could not replace image.");

        await viewModel.RegenerateImageCommand.ExecuteAsync(null);

        Assert.Equal(editedPayload, payloadReceivedByRegenerator);
        Assert.Same(secondImage, viewModel.PreviewImage);
        Assert.Equal(secondPath, viewModel.GeneratedImagePath);
        var regeneratedPayload = JObject.Parse(viewModel.Payload);
        Assert.Equal(0.125d, regeneratedPayload.Value<double>("stage_x"));
        Assert.Equal(-0.25d, regeneratedPayload.Value<double>("stage_y"));
        Assert.Equal("keep", regeneratedPayload.Value<string>("custom"));
        Assert.Equal(secondPath, regeneratedPayload.Value<string>("image_path"));
        Assert.False(viewModel.HasValidationError);
    }

    [Fact]
    public async Task RegenerateImage_WhenPayloadIsInvalid_KeepsCurrentPreviewAndPayload()
    {
        var firstImage = CreateTestBitmap(10, 20, 30);
        const string firstPath = @"C:\temp\first.png";
        const string invalidPayload = "{ invalid json";
        var viewModel = new ResponseSimulationDialogViewModel(
            "Move1 (Move)",
            "JSON",
            @"C:\exchange\response.json",
            "Index 7, command: move",
            invalidPayload,
            new ResponseSimulationPreview(firstImage, firstPath, invalidPayload),
            payload => Task.FromResult<ResponseSimulationPreview?>(
                new ResponseSimulationPreview(
                    firstImage,
                    @"C:\temp\second.png",
                    ResponseSimulationDialogService.SynchronizeJsonImagePath(
                        payload,
                        @"C:\temp\second.png"))),
            "Could not replace image.");

        await viewModel.RegenerateImageCommand.ExecuteAsync(null);

        Assert.Same(firstImage, viewModel.PreviewImage);
        Assert.Equal(firstPath, viewModel.GeneratedImagePath);
        Assert.Equal(invalidPayload, viewModel.Payload);
        Assert.Contains("Could not replace image", viewModel.ValidationMessage);
    }

    private static BitmapSource CreateTestBitmap(byte blue, byte green, byte red)
    {
        var pixels = new[]
        {
            blue, green, red, byte.MaxValue,
            blue, green, red, byte.MaxValue,
            blue, green, red, byte.MaxValue,
            blue, green, red, byte.MaxValue
        };
        var bitmap = BitmapSource.Create(
            2,
            2,
            96d,
            96d,
            PixelFormats.Bgra32,
            null,
            pixels,
            8);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] ReadPixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static string CreateTestDirectory()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "DrillFlow.Tests",
            "response-preview-" + Guid.NewGuid().ToString("N"));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
