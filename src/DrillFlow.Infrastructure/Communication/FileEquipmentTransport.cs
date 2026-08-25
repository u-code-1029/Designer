using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// Exchanges a single, atomically published request file for a matching response file. A fixed
/// sidecar opened with FileShare.None serializes the full exchange across processes and SMB
/// clients. Polling is deliberately the source of truth because it works for local folders and
/// UNC shares even when file-system change notifications are dropped.
/// </summary>
public sealed class FileEquipmentTransport : IEquipmentFileTransport, IDisposable
{
    private readonly EquipmentCommunicationOptions _options;
    private readonly ILogger<FileEquipmentTransport> _logger;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private bool _disposed;

    public FileEquipmentTransport(
        IOptions<EquipmentCommunicationOptions> options,
        ILogger<FileEquipmentTransport> logger)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;

        var validation = new EquipmentCommunicationOptionsValidator().Validate(null, _options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                EquipmentCommunicationOptions.SectionName,
                typeof(EquipmentCommunicationOptions),
                validation.Failures);
        }
    }

    public async Task<EquipmentResponseMessage> ExchangeAsync(
        EquipmentRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ThrowIfDisposed();
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Directory.CreateDirectory(_options.ExchangeDirectory);

            var exchangeLockPath = Path.Combine(
                _options.ExchangeDirectory,
                EquipmentCommunicationOptions.ExchangeLockFileName);
            using (await AcquireExchangeLockAsync(exchangeLockPath, cancellationToken).ConfigureAwait(false))
            {
                var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
                var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
                var serializedRequest = SerializeRequest(request);

                // In delete-after-read mode an existing request is never assumed to be stale and
                // overwritten. It may belong to a previous app run while the equipment still owns a
                // pending delete. Waiting for disappearance is the only safe first-run behavior.
                await EnsureRequestFileAbsentAsync(
                        requestPath,
                        EquipmentRequestDeletionWaitPhase.BeforeInitialPublish,
                        cancellationToken)
                    .ConfigureAwait(false);

                // A pre-existing retained response is only a baseline. Even if state was manually
                // rolled back, unchanged bytes are never accepted as the response to this exchange.
                var baselineResponse = await TryReadStableTextAsync(responsePath, cancellationToken)
                    .ConfigureAwait(false);

                var retryCount = _options.RetryEnabled ? _options.MaximumRetryCount : 0;
                var attempt = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (attempt > 0)
                    {
                        if (_options.RetryDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                        }

                        await EnsureRequestFileAbsentAsync(
                                requestPath,
                                EquipmentRequestDeletionWaitPhase.BeforeRetry,
                                cancellationToken)
                            .ConfigureAwait(false);

                        var responseBeforeRetry = await TryReadMatchingResponseOnceAsync(
                                responsePath,
                                request.Index,
                                baselineResponse,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (responseBeforeRetry is not null)
                        {
                            return await CompleteResponseAsync(
                                    requestPath,
                                    responsePath,
                                    responseBeforeRetry,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    attempt++;

                    await PublishRequestAsync(requestPath, serializedRequest, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogInformation(
                        "Published equipment command {Command} with correlation ID {CorrelationId} "
                        + "(attempt {Attempt}).",
                        request.Command,
                        request.Index,
                        attempt);

                    var response = await WaitForMatchingResponseAsync(
                            responsePath,
                            request.Index,
                            baselineResponse,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (response is not null)
                    {
                        return await CompleteResponseAsync(
                                requestPath,
                                responsePath,
                                response,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    // A timeout is still a completion boundary. In delete-after-read mode the method
                    // must not return (or retry) while the old pathname can be deleted later by the
                    // equipment, otherwise a caller's next request could be lost.
                    await EnsureRequestFileAbsentAsync(
                            requestPath,
                            EquipmentRequestDeletionWaitPhase.AfterResponseTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var lateResponse = await TryReadMatchingResponseOnceAsync(
                            responsePath,
                            request.Index,
                            baselineResponse,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (lateResponse is not null)
                    {
                        return await CompleteResponseAsync(
                                requestPath,
                                responsePath,
                                lateResponse,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (attempt > retryCount)
                    {
                        throw new EquipmentResponseTimeoutException(
                            request.Index,
                            attempt,
                            _options.ResponseTimeout);
                    }

                    _logger.LogWarning(
                        "Equipment response timed out for correlation ID {CorrelationId}; "
                        + "the identical request will be sent again ({Attempt}/{TotalAttempts}).",
                        request.Index,
                        attempt + 1,
                        retryCount + 1);
                }
            }
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exchangeGate.Dispose();
    }

    private async Task<FileStream> AcquireExchangeLockAsync(
        string lockFilePath,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var loggedContention = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);

                if (loggedContention)
                {
                    _logger.LogDebug("Acquired equipment exchange lock {LockFilePath}.", lockFilePath);
                }

                return stream;
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                if (!loggedContention)
                {
                    loggedContention = true;
                    _logger.LogDebug(
                        "Waiting for another controller to release equipment exchange lock "
                        + "{LockFilePath}.",
                        lockFilePath);
                }

                var remaining = _options.ResponseTimeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new EquipmentExchangeLockTimeoutException(
                        lockFilePath,
                        _options.ResponseTimeout,
                        exception);
                }

                var delay = remaining < _options.PollingInterval
                    ? remaining
                    : _options.PollingInterval;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError == 32 || nativeError == 33;
    }

    private static string SerializeRequest(EquipmentRequestMessage request)
    {
        var json = new JObject
        {
            ["index"] = request.Index,
            ["command"] = request.Command,
        };

        foreach (var parameter in request.Parameters)
        {
            json.Add(
                parameter.Key,
                parameter.Value is null ? JValue.CreateNull() : JToken.FromObject(parameter.Value));
        }

        var output = new StringBuilder();
        using (var stringWriter = new StringWriter(output, System.Globalization.CultureInfo.InvariantCulture))
        using (var jsonWriter = new ScientificNotationJsonTextWriter(stringWriter)
        {
            Formatting = Formatting.Indented,
        })
        {
            json.WriteTo(jsonWriter);
        }

        return output.ToString();
    }

    private async Task PublishRequestAsync(
        string requestPath,
        string serializedRequest,
        CancellationToken cancellationToken)
    {
        var tempPath = requestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(serializedRequest);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            var deadline = DateTime.UtcNow + _options.ResponseTimeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    AtomicFilePublisher.PublishCompletedTempFile(tempPath, requestPath);
                    return;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<EquipmentResponseMessage> CompleteResponseAsync(
        string requestPath,
        string responsePath,
        EquipmentResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureRequestFileAbsentAsync(
                requestPath,
                EquipmentRequestDeletionWaitPhase.AfterMatchingResponse,
                cancellationToken)
            .ConfigureAwait(false);

        if (_options.ApplicationResponseLifecycle == ApplicationResponseFileLifecycle.DeleteAfterRead)
        {
            await DeleteResponseAsync(responsePath, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Received equipment response for correlation ID {CorrelationId}.",
            response.Index);
        return response;
    }

    private async Task EnsureRequestFileAbsentAsync(
        string requestPath,
        EquipmentRequestDeletionWaitPhase phase,
        CancellationToken cancellationToken)
    {
        if (_options.EquipmentRequestLifecycle
            != EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)
        {
            return;
        }

        var elapsed = Stopwatch.StartNew();
        var loggedWait = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetRequestFilePresence(requestPath) == RequestFilePresence.Absent)
            {
                return;
            }

            if (!loggedWait)
            {
                loggedWait = true;
                _logger.LogDebug(
                    "Waiting for delete-after-read equipment to remove {RequestFilePath} "
                    + "during {WaitPhase}.",
                    requestPath,
                    phase);
            }

            var remaining = _options.ResponseTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new EquipmentRequestDeletionTimeoutException(
                    requestPath,
                    phase,
                    _options.ResponseTimeout);
            }

            var delay = remaining < _options.PollingInterval
                ? remaining
                : _options.PollingInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static RequestFilePresence GetRequestFilePresence(string requestPath)
    {
        try
        {
            File.GetAttributes(requestPath);
            return RequestFilePresence.Present;
        }
        catch (FileNotFoundException)
        {
            return RequestFilePresence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return RequestFilePresence.Absent;
        }
        catch (IOException)
        {
            // A transient network/share failure is not evidence that the equipment has deleted
            // the file. Failing closed avoids publishing into an uncertain pathname.
            return RequestFilePresence.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return RequestFilePresence.Unknown;
        }
    }

    private async Task<EquipmentResponseMessage?> TryReadMatchingResponseOnceAsync(
        string responsePath,
        int expectedIndex,
        string? baselineResponse,
        CancellationToken cancellationToken)
    {
        var json = await TryReadStableTextAsync(responsePath, cancellationToken)
            .ConfigureAwait(false);
        return json is not null
               && !string.Equals(json, baselineResponse, StringComparison.Ordinal)
               && TryParseMatchingResponse(json, expectedIndex, out var response)
            ? response
            : null;
    }

    private async Task<EquipmentResponseMessage?> WaitForMatchingResponseAsync(
        string responsePath,
        int expectedIndex,
        string? baselineResponse,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.ResponseTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await TryReadStableTextAsync(responsePath, cancellationToken)
                .ConfigureAwait(false);

            if (json is not null
                && !string.Equals(json, baselineResponse, StringComparison.Ordinal)
                && TryParseMatchingResponse(json, expectedIndex, out var response))
            {
                return response;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remaining < _options.PollingInterval
                ? remaining
                : _options.PollingInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> TryReadStableTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        FileSnapshot before;
        try
        {
            before = FileSnapshot.Capture(filePath);
            if (!before.Exists)
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        await Task.Delay(_options.StableReadDelay, cancellationToken).ConfigureAwait(false);

        try
        {
            var after = FileSnapshot.Capture(filePath);
            if (!after.Exists || !before.Equals(after))
            {
                return null;
            }

            using (var stream = new FileStream(
                       filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.Asynchronous))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
            {
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var final = FileSnapshot.Capture(filePath);
                return after.Equals(final) ? text : null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryParseMatchingResponse(
        string json,
        int expectedIndex,
        out EquipmentResponseMessage? response)
    {
        response = null;
        JObject document;
        try
        {
            document = JObject.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        var indexToken = document["index"];
        var commandToken = document["command"];
        if (indexToken?.Type != JTokenType.Integer
            || indexToken.Value<long>() != expectedIndex
            || commandToken?.Type != JTokenType.String
            || !string.Equals(commandToken.Value<string>(), "return", StringComparison.Ordinal))
        {
            return false;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.Properties())
        {
            if (string.Equals(property.Name, "index", StringComparison.Ordinal)
                || string.Equals(property.Name, "command", StringComparison.Ordinal))
            {
                continue;
            }

            properties[property.Name] = ConvertToken(property.Value);
        }

        response = new EquipmentResponseMessage(expectedIndex, "return", properties);
        return true;
    }

    private static object? ConvertToken(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Null:
            case JTokenType.Undefined:
                return null;
            case JTokenType.Boolean:
                return token.Value<bool>();
            case JTokenType.Integer:
                {
                    var value = token.Value<long>();
                    return value >= int.MinValue && value <= int.MaxValue ? (object)(int)value : value;
                }
            case JTokenType.Float:
                return token.Value<double>();
            case JTokenType.String:
                return token.Value<string>();
            case JTokenType.Date:
                return token.Value<DateTime>();
            case JTokenType.Guid:
                return token.Value<Guid>();
            case JTokenType.TimeSpan:
                return token.Value<TimeSpan>();
            case JTokenType.Array:
                {
                    var values = new List<object?>();
                    foreach (var child in token.Children())
                    {
                        values.Add(ConvertToken(child));
                    }

                    return new ReadOnlyCollection<object?>(values);
                }
            case JTokenType.Object:
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var property in ((JObject)token).Properties())
                    {
                        values[property.Name] = ConvertToken(property.Value);
                    }

                    return new ReadOnlyDictionary<string, object?>(values);
                }
            default:
                return token.ToString(Formatting.None, Array.Empty<JsonConverter>());
        }
    }

    private async Task DeleteResponseAsync(string responsePath, CancellationToken cancellationToken)
    {
        var maxDeleteWaitMilliseconds = Math.Min(
            2000d,
            Math.Max(100d, _options.PollingInterval.TotalMilliseconds * 20d));
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(maxDeleteWaitMilliseconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(responsePath))
                {
                    File.Delete(responsePath);
                }

                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileEquipmentTransport));
        }
    }

    private readonly struct FileSnapshot : IEquatable<FileSnapshot>
    {
        private FileSnapshot(bool exists, long length, DateTime lastWriteTimeUtc)
        {
            Exists = exists;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public bool Exists { get; }

        private long Length { get; }

        private DateTime LastWriteTimeUtc { get; }

        public static FileSnapshot Capture(string filePath)
        {
            var info = new FileInfo(filePath);
            info.Refresh();
            return info.Exists
                ? new FileSnapshot(true, info.Length, info.LastWriteTimeUtc)
                : new FileSnapshot(false, 0, default);
        }

        public bool Equals(FileSnapshot other)
        {
            return Exists == other.Exists
                   && Length == other.Length
                   && LastWriteTimeUtc == other.LastWriteTimeUtc;
        }

        public override bool Equals(object? obj)
        {
            return obj is FileSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Exists.GetHashCode() * 397)
                       ^ Length.GetHashCode()
                       ^ LastWriteTimeUtc.GetHashCode();
            }
        }
    }

    private enum RequestFilePresence
    {
        Absent,
        Present,
        Unknown,
    }

    private sealed class ScientificNotationJsonTextWriter : JsonTextWriter
    {
        public ScientificNotationJsonTextWriter(TextWriter textWriter)
            : base(textWriter)
        {
        }

        public override void WriteValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new JsonWriterException("NaN and infinity are not valid equipment request numbers.");
            }

            WriteRawValue(FormatScientific(value, "0.#################E+0"));
        }

        public override void WriteValue(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new JsonWriterException("NaN and infinity are not valid equipment request numbers.");
            }

            WriteRawValue(FormatScientific(value, "0.########E+0"));
        }

        public override void WriteValue(decimal value)
        {
            WriteRawValue(FormatScientific(value, "0.############################E+0"));
        }

        private static string FormatScientific<T>(T value, string format)
            where T : IFormattable
        {
            var text = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            var exponentMarker = text.IndexOf('E');
            if (exponentMarker < 0)
            {
                return text;
            }

            var mantissa = text.Substring(0, exponentMarker);
            var exponent = int.Parse(
                text.Substring(exponentMarker + 1),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture);
            var exponentText = exponent > 0
                ? "+" + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return mantissa + "E" + exponentText;
        }
    }
}
