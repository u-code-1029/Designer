using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Windows.Media;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopRuntimeResultImageTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task LoadImageAsync_UsesSharedStaDecoderForActionResult()
    {
        var imagePath = CreateTemporaryPath("png");
        File.WriteAllBytes(imagePath, Convert.FromBase64String(OnePixelPngBase64));
        try
        {
            using (var decoder = new LiveImageDecoder(NullLogger<LiveImageDecoder>.Instance))
            {
                var result = CreateResult(imagePath);

                var image = await result.LoadImageAsync(decoder, CancellationToken.None);

                Assert.NotNull(image);
                Assert.True(image!.IsFrozen);
            }
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public async Task LoadImageAsync_OversizedImageFailsNonFatally()
    {
        var imagePath = CreateTemporaryPath("bin");
        using (var stream = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(LiveImageSafetyLimits.MaximumEncodedBytes + 1);
        }

        try
        {
            using (var decoder = new LiveImageDecoder(NullLogger<LiveImageDecoder>.Instance))
            {
                var result = CreateResult(imagePath);

                var image = await result.LoadImageAsync(decoder, CancellationToken.None);

                Assert.Null(image);
            }
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public async Task WorkflowAction_ExposesDecodedLatestImageForCardBinding()
    {
        var imagePath = CreateTemporaryPath("bin");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
        try
        {
            var image = new DrawingImage();
            image.Freeze();
            var action = new WorkflowActionViewModel(
                new MoveNode { Key = "move_1" },
                new StubLocalizationService(),
                new StubImageDecoder(image));

            action.AddResult(CreateExecutionResult(imagePath));

            await WaitUntilAsync(() => action.HasLatestImage);
            Assert.True(action.HasLatestImagePath);
            Assert.Same(image, action.LatestImageSource);
            Assert.False(action.IsLatestImageLoading);
            Assert.False(action.HasLatestImageLoadError);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public async Task CompletingAnotherAction_DoesNotClearEarlierResultsOrDecodedImage()
    {
        var firstImagePath = CreateTemporaryPath("bin");
        var secondImagePath = CreateTemporaryPath("bin");
        File.WriteAllBytes(firstImagePath, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(secondImagePath, new byte[] { 4, 5, 6 });
        try
        {
            var decodedImage = new DrawingImage();
            decodedImage.Freeze();
            var localization = new StubLocalizationService();
            var decoder = new StubImageDecoder(decodedImage);
            var first = new WorkflowActionViewModel(
                new MoveNode { Key = "first" },
                localization,
                decoder);
            var second = new WorkflowActionViewModel(
                new MeasureNode { Key = "second" },
                localization,
                decoder);

            first.AddResult(CreateExecutionResult(firstImagePath));
            await WaitUntilAsync(() => first.HasLatestImage);
            var retainedResult = Assert.Single(first.Results);
            var retainedImage = first.LatestImageSource;

            second.AddResult(CreateExecutionResult(secondImagePath));
            await WaitUntilAsync(() => second.HasLatestImage);

            Assert.Same(retainedResult, Assert.Single(first.Results));
            Assert.Same(retainedImage, first.LatestImageSource);
            Assert.True(first.IsResultExpanded);
            Assert.Single(second.Results);
        }
        finally
        {
            TryDelete(firstImagePath);
            TryDelete(secondImagePath);
        }
    }

    [Fact]
    public async Task WorkflowAction_ReportsImagePathThatCannotBeLoaded()
    {
        var action = new WorkflowActionViewModel(
            new MoveNode { Key = "move_1" },
            new StubLocalizationService(),
            new StubImageDecoder(new DrawingImage()));

        action.AddResult(CreateExecutionResult(CreateTemporaryPath("missing")));

        await WaitUntilAsync(() => action.HasLatestImageLoadError);
        Assert.True(action.HasLatestImagePath);
        Assert.False(action.HasLatestImage);
        Assert.False(action.IsLatestImageLoading);
        Assert.Equal("ResultImageLoadFailed", action.LatestImageStatusText);
    }

    [Fact]
    public async Task WorkflowAction_ImageZoomIsBoundedAndResetsWithResultLifecycle()
    {
        var imagePath = CreateTemporaryPath("bin");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
        try
        {
            var image = new DrawingImage();
            image.Freeze();
            var action = new WorkflowActionViewModel(
                new MoveNode { Key = "move_1" },
                new StubLocalizationService(),
                new StubImageDecoder(image));
            action.AddResult(CreateExecutionResult(imagePath));
            await WaitUntilAsync(() => action.HasLatestImage);

            for (var index = 0; index < 20; index++)
            {
                action.ZoomResultImageOut();
            }

            Assert.Equal(WorkflowActionViewModel.MinimumResultImageZoom, action.ResultImageZoom);
            Assert.False(action.CanZoomResultImageOut);

            for (var index = 0; index < 20; index++)
            {
                action.ZoomResultImageIn();
            }

            Assert.Equal(WorkflowActionViewModel.MaximumResultImageZoom, action.ResultImageZoom);
            Assert.False(action.CanZoomResultImageIn);

            action.IsResultExpanded = false;
            action.AddResult(CreateExecutionResult(imagePath));
            Assert.Equal(1.0, action.ResultImageZoom);
            Assert.False(action.IsResultExpanded);

            action.ClearRuntime();
            Assert.Empty(action.Results);
            Assert.False(action.HasLatestImage);
            Assert.Equal(1.0, action.ResultImageZoom);
            Assert.True(action.IsResultExpanded);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public async Task WorkflowAction_RestoreRuntimeKeepsDecodedImageInMemory()
    {
        var imagePath = CreateTemporaryPath("bin");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
        try
        {
            var image = new DrawingImage();
            image.Freeze();
            var nodeId = Guid.NewGuid();
            var localization = new StubLocalizationService();
            var decoder = new StubImageDecoder(image);
            var source = new WorkflowActionViewModel(
                new MoveNode { Id = nodeId, Key = "before" },
                localization,
                decoder);
            source.AddResult(CreateExecutionResult(imagePath));
            await WaitUntilAsync(() => source.HasLatestImage);
            source.IsResultExpanded = false;
            source.ZoomResultImageIn();

            var restored = new WorkflowActionViewModel(
                new MoveNode { Id = nodeId, Key = "after" },
                localization,
                decoder);
            restored.RestoreRuntimeFrom(source);
            source.ClearRuntime();

            Assert.Single(restored.Results);
            Assert.Same(image, restored.LatestImageSource);
            Assert.False(restored.IsResultExpanded);
            Assert.Equal(1.25, restored.ResultImageZoom);
            Assert.Equal("after.result.image_path", restored.Results[0].Fields[0].ExpressionPath);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public async Task CutRuntimeSnapshot_RestoresMatchingSubtreeWithoutFieldEventLeak()
    {
        var imagePath = CreateTemporaryPath("bin");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
        try
        {
            var image = new DrawingImage();
            image.Freeze();
            var localization = new TrackingLocalizationService();
            var decoder = new StubImageDecoder(image);
            var repeatId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var source = CreateRepeatAction(repeatId, childId, "before", localization, decoder);
            var sourceChild = Assert.Single(source.Children);
            var sourceSubscriberCount = localization.SubscriberCount;
            sourceChild.AddResult(CreateExecutionResult(imagePath));
            await WaitUntilAsync(() => sourceChild.HasLatestImage);
            sourceChild.IsResultExpanded = false;
            sourceChild.ZoomResultImageIn();

            // Adding result fields must not attach more handlers to the singleton localization
            // service; Action/parameter subscriptions predate this result lifecycle change.
            Assert.Equal(sourceSubscriberCount, localization.SubscriberCount);
            var snapshot = CutActionRuntimeSnapshot.Capture(new[] { source });
            var restored = CreateRepeatAction(repeatId, childId, "after", localization, decoder);
            var restoredChild = Assert.Single(restored.Children);

            snapshot.RestoreTo(new[] { restored });
            snapshot.Clear();

            Assert.Empty(sourceChild.Results);
            Assert.Single(restoredChild.Results);
            Assert.Same(image, restoredChild.LatestImageSource);
            Assert.False(restoredChild.IsResultExpanded);
            Assert.Equal(1.25, restoredChild.ResultImageZoom);
            Assert.Equal("after.result.image_path", restoredChild.Results[0].Fields[0].ExpressionPath);
            Assert.Equal(sourceSubscriberCount * 2, localization.SubscriberCount);

            var changedProperties = new List<string?>();
            restoredChild.Results[0].Fields[0].PropertyChanged += (_, args) =>
                changedProperties.Add(args.PropertyName);
            localization.RaiseLanguageChanged();
            Assert.Equal(1, changedProperties.Count(name => name == "Description"));
            Assert.Equal(1, changedProperties.Count(name => name == "Label"));
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    [Fact]
    public async Task CutRuntimeSnapshot_DoesNotCopyResultsToNewIdentity()
    {
        var imagePath = CreateTemporaryPath("bin");
        File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
        try
        {
            var image = new DrawingImage();
            image.Freeze();
            var localization = new StubLocalizationService();
            var decoder = new StubImageDecoder(image);
            var source = new WorkflowActionViewModel(
                new MoveNode { Key = "source" },
                localization,
                decoder);
            source.AddResult(CreateExecutionResult(imagePath));
            await WaitUntilAsync(() => source.HasLatestImage);
            var snapshot = CutActionRuntimeSnapshot.Capture(new[] { source });
            var copied = new WorkflowActionViewModel(
                new MoveNode { Key = "copy" },
                localization,
                decoder);

            snapshot.RestoreTo(new[] { copied });
            snapshot.Clear();

            Assert.Empty(copied.Results);
            Assert.False(copied.HasLatestImage);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    private static RuntimeResultViewModel CreateResult(string imagePath)
    {
        return new RuntimeResultViewModel(
            CreateExecutionResult(imagePath),
            "action_1",
            new StubLocalizationService());
    }

    private static ActionExecutionResult CreateExecutionResult(string imagePath) => new ActionExecutionResult
    {
        CorrelationId = 10,
        Values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["image_path"] = imagePath
        }
    };

    private static WorkflowActionViewModel CreateRepeatAction(
        Guid repeatId,
        Guid childId,
        string childAlias,
        ILocalizationService localization,
        ILiveImageDecoder decoder)
    {
        var repeat = new RepeatNode
        {
            Id = repeatId,
            Key = "repeat_1",
            Body = new List<WorkflowNode>
            {
                new MoveNode { Id = childId, Key = childAlias }
            }
        };
        return new WorkflowActionViewModel(repeat, localization, decoder);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected image binding state was not reached.");
            }

            await Task.Delay(20);
        }
    }

    private static string CreateTemporaryPath(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DrillFlow.Tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + "." + extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StubLocalizationService : ILocalizationService
    {
#pragma warning disable CS0067
        public event EventHandler? LanguageChanged;
#pragma warning restore CS0067

        public string SelectedLanguage => "en-US";

        public string EffectiveLanguage => "en-US";

        public string this[string key] => key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
        }
    }

    private sealed class StubImageDecoder : ILiveImageDecoder
    {
        private readonly ImageSource _image;

        public StubImageDecoder(ImageSource image)
        {
            _image = image;
        }

        public Task<LiveImageDecodeResult> DecodeAsync(
            byte[] encodedImage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LiveImageDecodeResult(
                _image,
                1,
                1,
                96,
                96,
                ".png"));
        }
    }

    private sealed class TrackingLocalizationService : ILocalizationService
    {
        private EventHandler? _languageChanged;

        public event EventHandler? LanguageChanged
        {
            add
            {
                _languageChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _languageChanged -= value;
                SubscriberCount--;
            }
        }

        public int SubscriberCount { get; private set; }

        public string SelectedLanguage => "en-US";

        public string EffectiveLanguage => "en-US";

        public string this[string key] => key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
        }

        public void RaiseLanguageChanged() => _languageChanged?.Invoke(this, EventArgs.Empty);
    }
}
