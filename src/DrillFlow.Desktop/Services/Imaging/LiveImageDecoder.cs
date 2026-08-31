using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace DrillFlow.Desktop.Services;

/// <summary>
/// Serializes WIC work on one private STA thread. Frozen results can safely be consumed by WPF's
/// UI thread; queued work canceled by a newer stream lifecycle is skipped before decode.
/// </summary>
public sealed class LiveImageDecoder : ILiveImageDecoder, IDisposable
{
    private const int MaximumPreviewDimension = 1920;
    private const int DisposeJoinTimeoutMilliseconds = 5000;

    private readonly object _sync = new object();
    private readonly BlockingCollection<DecodeWorkItem> _queue = new BlockingCollection<DecodeWorkItem>();
    private readonly CancellationTokenSource _disposeCancellation = new CancellationTokenSource();
    private readonly ILogger<LiveImageDecoder> _logger;
    private readonly Func<byte[], CancellationToken, LiveImageDecodeResult> _decodeBackend;
    private readonly Thread _worker;
    private bool _disposed;

    public LiveImageDecoder(ILogger<LiveImageDecoder> logger)
        : this(logger, DecodeCore)
    {
    }

    internal LiveImageDecoder(
        ILogger<LiveImageDecoder> logger,
        Func<byte[], CancellationToken, LiveImageDecodeResult> decodeBackend)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _decodeBackend = decodeBackend ?? throw new ArgumentNullException(nameof(decodeBackend));
        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "DrillFlow Live Image Decoder"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    internal bool IsWorkerAlive => _worker.IsAlive;

    public Task<LiveImageDecodeResult> DecodeAsync(
        byte[] encodedImage,
        CancellationToken cancellationToken)
    {
        if (encodedImage is null)
        {
            throw new ArgumentNullException(nameof(encodedImage));
        }

        LiveImageSafetyLimits.ValidateEncodedByteLength(encodedImage.LongLength);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            var workItem = new DecodeWorkItem(
                encodedImage,
                cancellationToken,
                _disposeCancellation.Token);
            try
            {
                _queue.Add(workItem);
                return workItem.Task;
            }
            catch
            {
                workItem.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();
            _queue.CompleteAdding();
        }

        if (Thread.CurrentThread == _worker)
        {
            return;
        }

        if (!_worker.Join(DisposeJoinTimeoutMilliseconds))
        {
            // WIC itself is not cooperatively cancelable once native decode has begun. Input
            // limits bound the work; keep resources alive for the background worker to finish.
            _logger.LogWarning(
                "The live image decoder STA thread did not stop within {TimeoutMilliseconds} ms.",
                DisposeJoinTimeoutMilliseconds);
            return;
        }

        _queue.Dispose();
        _disposeCancellation.Dispose();
    }

    private void ProcessQueue()
    {
        try
        {
            foreach (var workItem in _queue.GetConsumingEnumerable())
            {
                if (workItem.Task.IsCompleted)
                {
                    workItem.Dispose();
                    continue;
                }

                workItem.Execute(_decodeBackend, _disposeCancellation.Token);
            }
        }
        catch (ObjectDisposedException)
        {
            // Host teardown after the queue has completed.
        }
        finally
        {
            while (_queue.TryTake(out var workItem))
            {
                workItem.CancelForDisposal();
                workItem.Dispose();
            }
        }
    }

    private static LiveImageDecodeResult DecodeCore(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var metadataStream = new MemoryStream(bytes, writable: false))
        {
            var decoder = BitmapDecoder.Create(
                metadataStream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                // Keep the encoded stream alive for this metadata pass and avoid eagerly caching
                // full-resolution pixels before the dimension/pixel-count safety checks run.
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("The image has no decodable frames.");
            }

            var frame = decoder.Frames[0];
            var pixelWidth = frame.PixelWidth;
            var pixelHeight = frame.PixelHeight;
            LiveImageSafetyLimits.ValidatePixelDimensions(pixelWidth, pixelHeight);
            var dpiX = NormalizeDpi(frame.DpiX);
            var dpiY = NormalizeDpi(frame.DpiY);
            var detectedExtension = GetPreferredFileExtension(decoder.CodecInfo?.FileExtensions);

            cancellationToken.ThrowIfCancellationRequested();
            using (var imageStream = new MemoryStream(bytes, writable: false))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                if (pixelWidth >= pixelHeight && pixelWidth > MaximumPreviewDimension)
                {
                    bitmap.DecodePixelWidth = MaximumPreviewDimension;
                }
                else if (pixelHeight > MaximumPreviewDimension)
                {
                    bitmap.DecodePixelHeight = MaximumPreviewDimension;
                }

                bitmap.StreamSource = imageStream;
                bitmap.EndInit();
                cancellationToken.ThrowIfCancellationRequested();
                bitmap.Freeze();
                return new LiveImageDecodeResult(
                    bitmap,
                    pixelWidth,
                    pixelHeight,
                    dpiX,
                    dpiY,
                    detectedExtension);
            }
        }
    }

    private static double NormalizeDpi(double dpi)
    {
        return dpi > 0d && !double.IsNaN(dpi) && !double.IsInfinity(dpi)
            ? dpi
            : 96d;
    }

    private static string GetPreferredFileExtension(string? fileExtensions)
    {
        var candidates = (fileExtensions ?? string.Empty).Split(
            new[] { ',', ';', ' ' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in candidates)
        {
            var extension = candidate.Trim().TrimStart('*');
            if (!extension.StartsWith(".", StringComparison.Ordinal))
            {
                extension = "." + extension;
            }

            if (extension.Length > 1
                && extension.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                return extension;
            }
        }

        return ".png";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LiveImageDecoder));
        }
    }

    private sealed class DecodeWorkItem : IDisposable
    {
        private readonly byte[] _bytes;
        private readonly CancellationToken _callerCancellation;
        private readonly TaskCompletionSource<LiveImageDecodeResult> _completion
            = new TaskCompletionSource<LiveImageDecodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _disposeRegistration;

        public DecodeWorkItem(
            byte[] bytes,
            CancellationToken callerCancellation,
            CancellationToken disposeCancellation)
        {
            _bytes = bytes;
            _callerCancellation = callerCancellation;
            _callerRegistration = callerCancellation.Register(() => _completion.TrySetCanceled());
            _disposeRegistration = disposeCancellation.Register(CancelForDisposal);
        }

        public Task<LiveImageDecodeResult> Task => _completion.Task;

        public void Execute(
            Func<byte[], CancellationToken, LiveImageDecodeResult> decodeBackend,
            CancellationToken disposeCancellation)
        {
            if (_completion.Task.IsCompleted)
            {
                Dispose();
                return;
            }

            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                           _callerCancellation,
                           disposeCancellation))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var result = decodeBackend(_bytes, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    _completion.TrySetResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                if (disposeCancellation.IsCancellationRequested)
                {
                    CancelForDisposal();
                }
                else
                {
                    _completion.TrySetCanceled();
                }
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                Dispose();
            }
        }

        public void CancelForDisposal()
        {
            _completion.TrySetException(new ObjectDisposedException(nameof(LiveImageDecoder)));
        }

        public void Dispose()
        {
            _callerRegistration.Dispose();
            _disposeRegistration.Dispose();
        }
    }
}
