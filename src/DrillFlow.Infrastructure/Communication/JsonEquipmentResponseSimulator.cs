using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Infrastructure.IO;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// JSON commissioning implementation. It deliberately does not take the equipment exchange lock:
/// the real transport owns that lock while waiting for this response. Publication still uses a
/// fully flushed temporary file and atomic pathname replacement.
/// </summary>
public sealed class JsonEquipmentResponseSimulator : IEquipmentResponseSimulator
{
    private readonly EquipmentCommunicationOptions _options;

    public JsonEquipmentResponseSimulator(IOptions<EquipmentCommunicationOptions> options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public string PayloadFormat => "JSON";

    public async Task<EquipmentResponseSimulationDraft> CreateDraftAsync(
        WorkflowNode node,
        int? fallbackCorrelationId,
        CancellationToken cancellationToken,
        string? generatedImagePath = null)
    {
        if (node == null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var activeRequest = await TryReadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        var index = activeRequest?.Index ?? fallbackCorrelationId ?? 1;
        var response = CreateTemplate(node, index, generatedImagePath);
        return new EquipmentResponseSimulationDraft(
            response.ToString(Formatting.Indented),
            responsePath,
            activeRequest);
    }

    public Task<EquipmentRequestSnapshot?> GetActiveRequestAsync(
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        // Opening an unavailable UNC path can block synchronously before FileStream exposes an
        // awaitable operation. Keep the live page dispatcher out of all active-request probes.
        return Task.Run(
            () => TryReadRequestAsync(requestPath, cancellationToken),
            CancellationToken.None);
    }

    public async Task<FrameResponseSimulationResult> TryPublishFrameResponseAsync(
        EquipmentRequestSnapshot expectedRequest,
        string generatedImagePath,
        CancellationToken cancellationToken)
    {
        if (expectedRequest == null)
        {
            throw new ArgumentNullException(nameof(expectedRequest));
        }

        if (!EquipmentResponseMessage.IsSupportedAbsoluteImagePath(generatedImagePath))
        {
            throw new ArgumentException(
                "A generated frame image path must be an absolute local or UNC pathname.",
                nameof(generatedImagePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var activeRequest = await GetActiveRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        var readiness = GetFrameRequestReadiness(expectedRequest, activeRequest, responsePath);
        if (readiness != null)
        {
            return readiness;
        }

        Directory.CreateDirectory(_options.ExchangeDirectory);

        var existingResponse = await TryReadResponseSnapshotAsync(responsePath, cancellationToken)
            .ConfigureAwait(false);
        if (existingResponse.Exists
            && (!existingResponse.Index.HasValue
                || existingResponse.Index.Value == expectedRequest.Index))
        {
            // An unreadable response is treated as controller-owned. The simulator waits instead
            // of replacing bytes that may still be in the process of being committed.
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ResponseAlreadyExists,
                responsePath,
                activeRequest);
        }

        var response = new JObject
        {
            ["index"] = expectedRequest.Index,
            ["command"] = "return",
            ["stage_x"] = 0d,
            ["stage_y"] = 0d,
            ["image_path"] = Path.GetFullPath(generatedImagePath)
        };
        var payload = response.ToString(Formatting.Indented);
        var tempPath = responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(payload);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            activeRequest = await GetActiveRequestAsync(cancellationToken)
                .ConfigureAwait(false);
            readiness = GetFrameRequestReadiness(expectedRequest, activeRequest, responsePath);
            if (readiness != null)
            {
                return readiness;
            }

            // Give a real controller the final word. In the normal delete-response lifecycle a
            // CreateNew-style move cannot overwrite a response that appeared after our check.
            var latestResponse = await TryReadResponseSnapshotAsync(responsePath, cancellationToken)
                .ConfigureAwait(false);
            if (latestResponse.Exists
                && (!latestResponse.Index.HasValue
                    || latestResponse.Index.Value == expectedRequest.Index))
            {
                return new FrameResponseSimulationResult(
                    FrameResponseSimulationStatus.ResponseAlreadyExists,
                    responsePath,
                    activeRequest);
            }

            if (_options.EquipmentRequestLifecycle
                == EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)
            {
                await DeleteMatchingRequestAsync(
                        expectedRequest.Index,
                        expectedRequest.Command,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!latestResponse.Exists)
            {
                try
                {
                    File.Move(tempPath, responsePath);
                }
                catch (IOException) when (File.Exists(responsePath))
                {
                    return new FrameResponseSimulationResult(
                        FrameResponseSimulationStatus.ResponseAlreadyExists,
                        responsePath,
                        activeRequest);
                }
            }
            else
            {
                // A retained response from an older correlation is the transport baseline. It
                // must be replaced for the next frame to be observable.
                AtomicFilePublisher.PublishCompletedTempFile(tempPath, responsePath);
            }

            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.Published,
                responsePath,
                expectedRequest);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public ResponsePayloadValidationResult ValidatePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ResponsePayloadValidationResult.Failure("Response payload is empty.");
        }

        JObject root;
        try
        {
            root = JObject.Parse(
                payload,
                new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
        }
        catch (JsonException exception)
        {
            return ResponsePayloadValidationResult.Failure("Invalid JSON: " + exception.Message);
        }

        if (!HasValidResponsePropertyNames(root))
        {
            return ResponsePayloadValidationResult.Failure(
                "Response property names must be unique ignoring case, use canonical casing, "
                + "and cannot conflict with runtime metadata.");
        }

        if (!TryReadPositiveIndex(root["index"], out _))
        {
            return ResponsePayloadValidationResult.Failure("'index' must be a positive 32-bit integer.");
        }

        if (root["command"]?.Type != JTokenType.String
            || !string.Equals(root["command"]!.Value<string>(), "return", StringComparison.Ordinal))
        {
            return ResponsePayloadValidationResult.Failure("'command' must be exactly 'return'.");
        }

        if (!TryReadFiniteNumber(root["stage_x"], out _))
        {
            return ResponsePayloadValidationResult.Failure(
                "'stage_x' must be a finite JSON number in meters.");
        }

        if (!TryReadFiniteNumber(root["stage_y"], out _))
        {
            return ResponsePayloadValidationResult.Failure(
                "'stage_y' must be a finite JSON number in meters.");
        }

        var imagePath = root["image_path"];
        if (imagePath != null
            && (imagePath.Type != JTokenType.String
                || !EquipmentResponseMessage.IsSupportedAbsoluteImagePath(imagePath.Value<string>())))
        {
            return ResponsePayloadValidationResult.Failure(
                "'image_path' must be an absolute local or UNC pathname when provided.");
        }

        return ResponsePayloadValidationResult.Success;
    }

    public async Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        var validation = ValidatePayload(payload);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_options.ExchangeDirectory);
        var responseRoot = JObject.Parse(payload);
        TryReadPositiveIndex(responseRoot["index"], out var responseIndex);
        if (_options.EquipmentRequestLifecycle
            == EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)
        {
            await DeleteMatchingRequestAsync(responseIndex, null, cancellationToken)
                .ConfigureAwait(false);
        }

        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var tempPath = responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(payload);
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

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFilePublisher.PublishCompletedTempFile(tempPath, responsePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task DeleteMatchingRequestAsync(
        int responseIndex,
        string? expectedCommand,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var activeRequest = await TryReadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (activeRequest == null)
        {
            // The real controller (or a previous test step) may already have consumed it.
            return;
        }

        if (activeRequest.Index != responseIndex
            || (expectedCommand != null
                && !string.Equals(
                    activeRequest.Command,
                    expectedCommand,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The active request is {activeRequest.Command}#{activeRequest.Index}, but the simulated "
                + $"response belongs to {expectedCommand ?? "<unspecified>"}#{responseIndex}. The request "
                + "was not deleted.");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(requestPath);
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
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

    private static JObject CreateTemplate(
        WorkflowNode node,
        int index,
        string? generatedImagePath)
    {
        if (!(node is MoveNode)
            && !(node is MeasureNode)
            && !(node is DrillNode)
            && !(node is AbortNode))
        {
            throw new ArgumentException(
                "Only equipment actions have a response payload.",
                nameof(node));
        }

        if (!string.IsNullOrWhiteSpace(generatedImagePath)
            && !EquipmentResponseMessage.IsSupportedAbsoluteImagePath(generatedImagePath))
        {
            throw new ArgumentException(
                "A generated response image path must be an absolute local or UNC pathname.",
                nameof(generatedImagePath));
        }

        var response = new JObject
        {
            ["index"] = index,
            ["command"] = "return",
            ["stage_x"] = 0d,
            ["stage_y"] = 0d
        };

        if (!string.IsNullOrWhiteSpace(generatedImagePath))
        {
            response["image_path"] = generatedImagePath;
        }

        return response;
    }

    private static async Task<EquipmentRequestSnapshot?> TryReadRequestAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json;
        try
        {
            using (var stream = new FileStream(
                       requestPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.Asynchronous))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = JObject.Parse(json);
            var index = root["index"];
            var command = root["command"];
            if (!TryReadPositiveIndex(index, out var requestIndex)
                || command?.Type != JTokenType.String)
            {
                return null;
            }

            return new EquipmentRequestSnapshot(requestIndex, command.Value<string>()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FrameResponseSimulationResult? GetFrameRequestReadiness(
        EquipmentRequestSnapshot expectedRequest,
        EquipmentRequestSnapshot? activeRequest,
        string responsePath)
    {
        if (activeRequest == null)
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.NoActiveRequest,
                responsePath);
        }

        if (!string.Equals(activeRequest.Command, "frame", StringComparison.Ordinal))
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ActiveRequestIsNotFrame,
                responsePath,
                activeRequest);
        }

        if (activeRequest.Index != expectedRequest.Index
            || !string.Equals(
                activeRequest.Command,
                expectedRequest.Command,
                StringComparison.Ordinal))
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ActiveRequestChanged,
                responsePath,
                activeRequest);
        }

        return null;
    }

    private static async Task<ResponseFileSnapshot> TryReadResponseSnapshotAsync(
        string responsePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json;
        try
        {
            using (var stream = new FileStream(
                       responsePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.Asynchronous))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            return ResponseFileSnapshot.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return ResponseFileSnapshot.Absent;
        }
        catch (IOException)
        {
            return File.Exists(responsePath)
                ? ResponseFileSnapshot.Unreadable
                : ResponseFileSnapshot.Absent;
        }
        catch (UnauthorizedAccessException)
        {
            return ResponseFileSnapshot.Unreadable;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = JObject.Parse(json);
            return TryReadPositiveIndex(root["index"], out var index)
                ? new ResponseFileSnapshot(true, index)
                : ResponseFileSnapshot.Unreadable;
        }
        catch (JsonException)
        {
            return ResponseFileSnapshot.Unreadable;
        }
    }

    private static bool TryReadPositiveIndex(JToken? token, out int index)
    {
        index = 0;
        return token?.Type == JTokenType.Integer
               && int.TryParse(
                   token.ToString(Formatting.None),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out index)
               && index > 0;
    }

    private static bool HasValidResponsePropertyNames(JObject document)
    {
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.Properties())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !names.Add(property.Name))
            {
                return false;
            }

            if (string.Equals(property.Name, "iteration_path", StringComparison.OrdinalIgnoreCase)
                || IsNonCanonicalProperty(property.Name, "index")
                || IsNonCanonicalProperty(property.Name, "command")
                || IsNonCanonicalProperty(property.Name, "stage_x")
                || IsNonCanonicalProperty(property.Name, "stage_y")
                || IsNonCanonicalProperty(property.Name, "image_path"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNonCanonicalProperty(string propertyName, string canonicalName)
    {
        return string.Equals(propertyName, canonicalName, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(propertyName, canonicalName, StringComparison.Ordinal);
    }

    private static bool TryReadFiniteNumber(JToken? token, out double value)
    {
        value = 0d;
        if (token?.Type != JTokenType.Integer && token?.Type != JTokenType.Float)
        {
            return false;
        }

        try
        {
            value = token.Value<double>();
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
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

    private sealed class ResponseFileSnapshot
    {
        public static ResponseFileSnapshot Absent { get; }
            = new ResponseFileSnapshot(false, null);

        public static ResponseFileSnapshot Unreadable { get; }
            = new ResponseFileSnapshot(true, null);

        public ResponseFileSnapshot(bool exists, int? index)
        {
            Exists = exists;
            Index = index;
        }

        public bool Exists { get; }

        public int? Index { get; }
    }
}
