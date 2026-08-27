using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// Keeps the commissioning editor's readable JSON logical shape while probing and publishing the
/// same fixed-template XML wire payloads used by the real transport.
/// </summary>
public sealed class JsonEquipmentResponseSimulator : IEquipmentResponseSimulator
{
    private readonly EquipmentCommunicationOptions _options;
    private readonly IEquipmentMessageCodec _codec;

    public JsonEquipmentResponseSimulator(IOptions<EquipmentCommunicationOptions> options)
        : this(options, new XmlTemplateEquipmentMessageCodec())
    {
    }

    public JsonEquipmentResponseSimulator(
        IOptions<EquipmentCommunicationOptions> options,
        IEquipmentMessageCodec codec)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    // The dialog edits a logical JSON document. PublishAsync converts it to XML before writing.
    public string PayloadFormat => "JSON";

    public async Task<EquipmentResponseSimulationDraft> CreateDraftAsync(
        WorkflowNode node,
        int? fallbackCorrelationId,
        CancellationToken cancellationToken,
        string? generatedImagePath = null)
    {
        if (node is null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        var action = GetAction(node);
        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var activeRequest = await GetActiveRequestAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = activeRequest != null
                            && string.Equals(activeRequest.Action, action, StringComparison.Ordinal)
            ? activeRequest.CorrelationId
            : fallbackCorrelationId ?? 1;
        var response = CreateDefaultResponse(
            action,
            correlationId,
            activeRequest,
            generatedImagePath);

        return new EquipmentResponseSimulationDraft(
            SerializeLogicalJson(response),
            responsePath,
            activeRequest);
    }

    public Task<EquipmentRequestSnapshot?> GetActiveRequestAsync(
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        return Task.Run(
            async () =>
            {
                var payload = await TryReadBytesAsync(requestPath, cancellationToken)
                    .ConfigureAwait(false);
                if (payload is null || !_codec.TryDeserializeRequest(payload, out var request)
                                    || request is null)
                {
                    return null;
                }

                return new EquipmentRequestSnapshot(
                    request.CorrelationId,
                    request.Action,
                    request.Parameters);
            },
            CancellationToken.None);
    }

    public async Task<FrameResponseSimulationResult> TryPublishFrameResponseAsync(
        EquipmentRequestSnapshot expectedRequest,
        string generatedImagePath,
        CancellationToken cancellationToken)
    {
        if (expectedRequest is null)
        {
            throw new ArgumentNullException(nameof(expectedRequest));
        }

        if (!string.Equals(expectedRequest.Action, EquipmentActionNames.Live, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The continuous response simulator requires a live request snapshot.",
                nameof(expectedRequest));
        }

        if (!EquipmentResponseMessage.IsSupportedAbsoluteImagePath(generatedImagePath))
        {
            throw new ArgumentException(
                "A generated live image path must be an absolute local or UNC pathname.",
                nameof(generatedImagePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var activeRequest = await GetActiveRequestAsync(cancellationToken).ConfigureAwait(false);
        var readiness = GetLiveRequestReadiness(expectedRequest, activeRequest, responsePath);
        if (readiness != null)
        {
            return readiness;
        }

        Directory.CreateDirectory(_options.ExchangeDirectory);
        var existingResponse = await TryReadResponseSnapshotAsync(responsePath, cancellationToken)
            .ConfigureAwait(false);
        if (existingResponse.Exists
            && (!existingResponse.CorrelationId.HasValue
                || existingResponse.CorrelationId.Value == expectedRequest.CorrelationId))
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ResponseAlreadyExists,
                responsePath,
                activeRequest);
        }

        var response = new EquipmentResponseMessage(
            expectedRequest.CorrelationId,
            EquipmentActionNames.Live,
            0,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hfw"] = GetLiveHfw(expectedRequest),
                ["frame_count"] = 1,
                ["image_path"] = Path.GetFullPath(generatedImagePath)
            });
        var bytes = _codec.SerializeResponse(response);
        var tempPath = responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteCompletedTempFileAsync(tempPath, bytes, cancellationToken)
                .ConfigureAwait(false);

            activeRequest = await GetActiveRequestAsync(cancellationToken).ConfigureAwait(false);
            readiness = GetLiveRequestReadiness(expectedRequest, activeRequest, responsePath);
            if (readiness != null)
            {
                return readiness;
            }

            var latestResponse = await TryReadResponseSnapshotAsync(responsePath, cancellationToken)
                .ConfigureAwait(false);
            if (latestResponse.Exists
                && (!latestResponse.CorrelationId.HasValue
                    || latestResponse.CorrelationId.Value == expectedRequest.CorrelationId))
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
                        expectedRequest.CorrelationId,
                        expectedRequest.Action,
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
        if (!TryParseLogicalResponseJson(payload, out var response, out var error))
        {
            return ResponsePayloadValidationResult.Failure(error);
        }

        try
        {
            _ = _codec.SerializeResponse(response!);
            return ResponsePayloadValidationResult.Success;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          || exception is InvalidDataException
                                          || exception is InvalidOperationException)
        {
            return ResponsePayloadValidationResult.Failure(exception.Message);
        }
    }

    public async Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        if (!TryParseLogicalResponseJson(payload, out var response, out var error)
            || response is null)
        {
            throw new InvalidDataException(error);
        }

        var bytes = _codec.SerializeResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_options.ExchangeDirectory);

        if (_options.EquipmentRequestLifecycle
            == EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)
        {
            await DeleteMatchingRequestAsync(
                    response.CorrelationId,
                    response.Action,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var tempPath = responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteCompletedTempFileAsync(tempPath, bytes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFilePublisher.PublishCompletedTempFile(tempPath, responsePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task DeleteMatchingRequestAsync(
        int responseCorrelationId,
        string? expectedAction,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var payload = await TryReadBytesAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (payload is null || !_codec.TryDeserializeRequest(payload, out var activeRequest)
                            || activeRequest is null)
        {
            return;
        }

        if (activeRequest.CorrelationId != responseCorrelationId
            || expectedAction != null
            && !string.Equals(activeRequest.Action, expectedAction, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The active request is {activeRequest.Action}#{activeRequest.CorrelationId}, but "
                + $"the simulated response belongs to {expectedAction ?? "<unspecified>"}#"
                + $"{responseCorrelationId}. The request was not deleted.");
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

    private static EquipmentResponseMessage CreateDefaultResponse(
        string action,
        int correlationId,
        EquipmentRequestSnapshot? activeRequest,
        string? generatedImagePath)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (action)
        {
            case EquipmentActionNames.Stage:
                properties["current_stage_x"] = 0d;
                properties["current_stage_y"] = 0d;
                break;
            case EquipmentActionNames.Camera:
                properties["current_camera_x"] = 0d;
                properties["current_camera_y"] = 0d;
                break;
            case EquipmentActionNames.Focus:
                properties["z_to_sharpness_2d"] = Array.Empty<IReadOnlyList<double>>();
                break;
            case EquipmentActionNames.Integration:
                properties["hfw"] = GetNumberOrDefault(activeRequest, "hfw", 1E-3d);
                properties["frame_count"] = GetIntegerOrDefault(activeRequest, "frame_count", 8);
                properties["image_path"] = GetSimulationImagePath(generatedImagePath);
                break;
            case EquipmentActionNames.Live:
                properties["hfw"] = GetNumberOrDefault(activeRequest, "hfw", 1E-3d);
                properties["frame_count"] = 1;
                properties["image_path"] = GetSimulationImagePath(generatedImagePath);
                break;
        }

        return new EquipmentResponseMessage(correlationId, action, 0, properties);
    }

    private static string GetAction(WorkflowNode node)
    {
        switch (node)
        {
            case StageNode _: return EquipmentActionNames.Stage;
            case CameraNode _: return EquipmentActionNames.Camera;
            case FocusNode _: return EquipmentActionNames.Focus;
            case IntegrationNode _: return EquipmentActionNames.Integration;
            case LiveNode _: return EquipmentActionNames.Live;
            case AbortNode _: return EquipmentActionNames.Abort;
            default:
                throw new ArgumentException(
                    "Only equipment actions have an equipment response payload.",
                    nameof(node));
        }
    }

    private static string GetSimulationImagePath(string? generatedImagePath)
    {
        var path = string.IsNullOrWhiteSpace(generatedImagePath)
            ? @"C:\DrillFlow\Images\simulated.png"
            : generatedImagePath!;
        if (!EquipmentResponseMessage.IsSupportedAbsoluteImagePath(path))
        {
            throw new ArgumentException(
                "A generated response image path must be an absolute local or UNC pathname.",
                nameof(generatedImagePath));
        }

        return path;
    }

    private static double GetLiveHfw(EquipmentRequestSnapshot request)
    {
        var hfw = GetNumberOrDefault(request, "hfw", 1E-3d);
        return hfw > 0d && hfw < XmlTemplateEquipmentMessageCodec.MaximumHfwMetres
            ? hfw
            : 1E-3d;
    }

    private static double GetNumberOrDefault(
        EquipmentRequestSnapshot? request,
        string name,
        double fallback)
    {
        if (request != null
            && request.Parameters.TryGetValue(name, out var value)
            && TryConvertNumber(value, out var number))
        {
            return number;
        }

        return fallback;
    }

    private static int GetIntegerOrDefault(
        EquipmentRequestSnapshot? request,
        string name,
        int fallback)
    {
        var number = GetNumberOrDefault(request, name, fallback);
        return number == Math.Truncate(number) && number >= int.MinValue && number <= int.MaxValue
            ? (int)number
            : fallback;
    }

    private static string SerializeLogicalJson(EquipmentResponseMessage response)
    {
        var root = new JObject
        {
            ["type"] = response.Type,
            ["correlation_id"] = response.CorrelationId,
            ["action"] = response.Action,
            ["result"] = response.Result
        };
        foreach (var property in response.Properties)
        {
            root.Add(
                property.Key,
                property.Value is null ? JValue.CreateNull() : JToken.FromObject(property.Value));
        }

        return root.ToString(Formatting.Indented);
    }

    private static bool TryParseLogicalResponseJson(
        string payload,
        out EquipmentResponseMessage? response,
        out string error)
    {
        response = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "Response payload is empty.";
            return false;
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
            error = "Invalid JSON: " + exception.Message;
            return false;
        }

        if (!HasCanonicalLogicalPropertyNames(root))
        {
            error = "Logical response property names must be unique ignoring case and use canonical casing.";
            return false;
        }

        if (root["type"]?.Type != JTokenType.String
            || !string.Equals(root["type"]!.Value<string>(), "response", StringComparison.Ordinal))
        {
            error = "'type' must be exactly 'response'.";
            return false;
        }

        if (!TryReadPositiveCorrelationId(root["correlation_id"], out var correlationId))
        {
            error = "'correlation_id' must be a positive 32-bit integer.";
            return false;
        }

        if (root["action"]?.Type != JTokenType.String
            || !EquipmentActionNames.IsKnown(root["action"]!.Value<string>()))
        {
            error = "'action' must be one of stage, camera, focus, integration, live, or abort.";
            return false;
        }

        if (root["result"]?.Type != JTokenType.Integer
            || !int.TryParse(
                root["result"]!.ToString(Formatting.None),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result)
            || result < 0
            || result > 1)
        {
            error = "'result' must be 0 (success) or 1 (failure).";
            return false;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in root.Properties())
        {
            if (property.Name == "type"
                || property.Name == "correlation_id"
                || property.Name == "action"
                || property.Name == "result")
            {
                continue;
            }

            properties.Add(property.Name, ConvertToken(property.Value));
        }

        try
        {
            response = new EquipmentResponseMessage(
                correlationId,
                root["action"]!.Value<string>()!,
                result,
                properties);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          || exception is InvalidOperationException)
        {
            error = exception.Message;
            response = null;
            return false;
        }
    }

    private static bool HasCanonicalLogicalPropertyNames(JObject root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.Properties())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !names.Add(property.Name))
            {
                return false;
            }

            if (IsNonCanonical(property.Name, "type")
                || IsNonCanonical(property.Name, "correlation_id")
                || IsNonCanonical(property.Name, "action")
                || IsNonCanonical(property.Name, "result"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNonCanonical(string actual, string canonical)
    {
        return string.Equals(actual, canonical, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(actual, canonical, StringComparison.Ordinal);
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
                    var text = token.ToString(Formatting.None);
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    {
                        return intValue;
                    }

                    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                        ? (object)longValue
                        : text;
                }
            case JTokenType.Float:
                return token.Value<double>();
            case JTokenType.String:
                return token.Value<string>();
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
                        values.Add(property.Name, ConvertToken(property.Value));
                    }

                    return new ReadOnlyDictionary<string, object?>(values);
                }
            default:
                return token.ToString(Formatting.None, Array.Empty<JsonConverter>());
        }
    }

    private static bool TryReadPositiveCorrelationId(JToken? token, out int correlationId)
    {
        correlationId = 0;
        return token?.Type == JTokenType.Integer
               && int.TryParse(
                   token.ToString(Formatting.None),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out correlationId)
               && correlationId > 0;
    }

    private static FrameResponseSimulationResult? GetLiveRequestReadiness(
        EquipmentRequestSnapshot expectedRequest,
        EquipmentRequestSnapshot? activeRequest,
        string responsePath)
    {
        if (activeRequest is null)
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.NoActiveRequest,
                responsePath);
        }

        if (!string.Equals(activeRequest.Action, EquipmentActionNames.Live, StringComparison.Ordinal))
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ActiveRequestIsNotLive,
                responsePath,
                activeRequest);
        }

        if (activeRequest.CorrelationId != expectedRequest.CorrelationId
            || !string.Equals(activeRequest.Action, expectedRequest.Action, StringComparison.Ordinal))
        {
            return new FrameResponseSimulationResult(
                FrameResponseSimulationStatus.ActiveRequestChanged,
                responsePath,
                activeRequest);
        }

        return null;
    }

    private async Task<ResponseFileSnapshot> TryReadResponseSnapshotAsync(
        string responsePath,
        CancellationToken cancellationToken)
    {
        var payload = await TryReadBytesAsync(responsePath, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return File.Exists(responsePath)
                ? ResponseFileSnapshot.Unreadable
                : ResponseFileSnapshot.Absent;
        }

        return _codec.TryDeserializeResponse(payload, out var response) && response != null
            ? new ResponseFileSnapshot(true, response.CorrelationId)
            : ResponseFileSnapshot.Unreadable;
    }

    private static async Task<byte[]?> TryReadBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.Asynchronous))
            {
                var length = stream.Length;
                if (length > EquipmentMessageLimits.MaximumWirePayloadBytes)
                {
                    return null;
                }

                var bytes = new byte[(int)length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await stream.ReadAsync(
                            bytes,
                            offset,
                            bytes.Length - offset,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return null;
                    }

                    offset += read;
                }

                return bytes;
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
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteCompletedTempFileAsync(
        string tempPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
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
    }

    private static bool TryConvertNumber(object? value, out double number)
    {
        number = 0d;
        try
        {
            switch (value)
            {
                case byte item: number = item; break;
                case sbyte item: number = item; break;
                case short item: number = item; break;
                case ushort item: number = item; break;
                case int item: number = item; break;
                case uint item: number = item; break;
                case long item: number = item; break;
                case ulong item: number = item; break;
                case float item: number = item; break;
                case double item: number = item; break;
                case decimal item: number = Convert.ToDouble(item, CultureInfo.InvariantCulture); break;
                default: return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return !double.IsNaN(number) && !double.IsInfinity(number);
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
        public static ResponseFileSnapshot Absent { get; } = new ResponseFileSnapshot(false, null);

        public static ResponseFileSnapshot Unreadable { get; } = new ResponseFileSnapshot(true, null);

        public ResponseFileSnapshot(bool exists, int? correlationId)
        {
            Exists = exists;
            CorrelationId = correlationId;
        }

        public bool Exists { get; }

        public int? CorrelationId { get; }
    }
}
