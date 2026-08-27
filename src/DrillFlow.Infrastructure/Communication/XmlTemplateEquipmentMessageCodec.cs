using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DrillFlow.Application.Communication;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// Renders and extracts the equipment's fixed XML answer-sheet templates. This is intentionally
/// not an XML object serializer: every non-placeholder byte comes from one of the twelve embedded
/// contract templates, while only declared scalar values are replaced or extracted.
/// </summary>
public sealed class XmlTemplateEquipmentMessageCodec : IEquipmentMessageCodec
{
    public const double MaximumHfwMetres = 2.4E-3d;
    public const int MaximumIntegrationFrameCount = 64;

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly IReadOnlyDictionary<string, ActionTemplates> _templates;

    public XmlTemplateEquipmentMessageCodec()
        : this(LoadEmbeddedTemplate)
    {
    }

    internal XmlTemplateEquipmentMessageCodec(Func<string, string, string> templateLoader)
    {
        if (templateLoader is null)
        {
            throw new ArgumentNullException(nameof(templateLoader));
        }

        var templates = new Dictionary<string, ActionTemplates>(StringComparer.Ordinal);
        foreach (var action in EquipmentActionNames.All)
        {
            var requestFields = GetExpectedRequestFields(action);
            var responseFields = GetExpectedResponseFields(action);
            templates.Add(
                action,
                new ActionTemplates(
                    TemplateDefinition.Parse(
                        action,
                        "request",
                        templateLoader(action, "request"),
                        requestFields),
                    TemplateDefinition.Parse(
                        action,
                        "response",
                        templateLoader(action, "response"),
                        responseFields)));
        }

        _templates = new ReadOnlyDictionary<string, ActionTemplates>(templates);
    }

    public string WireFormat => "XML template";

    public byte[] SerializeRequest(EquipmentRequestMessage request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var fields = CreateRequestFields(request);
        return EncodeWithinLimit(_templates[request.Action].Request.Render(fields));
    }

    public byte[] SerializeResponse(EquipmentResponseMessage response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var fields = CreateResponseFields(response);
        return EncodeWithinLimit(_templates[response.Action].Response.Render(fields));
    }

    public bool TryDeserializeRequest(byte[] payload, out EquipmentRequestMessage? request)
    {
        request = null;
        if (!TryDecode(payload, out var text))
        {
            return false;
        }

        foreach (var action in EquipmentActionNames.All)
        {
            if (!_templates[action].Request.TryExtract(text, out var extracted)
                || !TryCreateRequest(action, extracted, out request))
            {
                continue;
            }

            return true;
        }

        request = null;
        return false;
    }

    public bool TryDeserializeResponse(byte[] payload, out EquipmentResponseMessage? response)
    {
        response = null;
        if (!TryDecode(payload, out var text))
        {
            return false;
        }

        foreach (var action in EquipmentActionNames.All)
        {
            if (!_templates[action].Response.TryExtract(text, out var extracted)
                || !TryCreateResponse(action, extracted, out response))
            {
                continue;
            }

            return true;
        }

        response = null;
        return false;
    }

    public bool TryDeserializeResponse(
        byte[] payload,
        EquipmentRequestMessage expectedRequest,
        out EquipmentResponseMessage? response)
    {
        response = null;
        if (expectedRequest is null || !TryDecode(payload, out var text))
        {
            return false;
        }

        return _templates[expectedRequest.Action].Response.TryExtract(text, out var extracted)
               && TryCreateResponse(expectedRequest.Action, extracted, out response)
               && response!.CorrelationId == expectedRequest.CorrelationId
               && string.Equals(response.Action, expectedRequest.Action, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CreateRequestFields(EquipmentRequestMessage request)
    {
        var fields = CreateEnvelopeFields(request.Type, request.CorrelationId, request.Action);
        switch (request.Action)
        {
            case EquipmentActionNames.Stage:
                AddMoveMode(request.Parameters, "move_mode", fields);
                AddFiniteNumber(request.Parameters, "stage_x", fields);
                AddFiniteNumber(request.Parameters, "stage_y", fields);
                EnsureExactProperties(request.Parameters, "move_mode", "stage_x", "stage_y");
                break;

            case EquipmentActionNames.Camera:
                AddMoveMode(request.Parameters, "move_mode", fields);
                AddFiniteNumber(request.Parameters, "camera_x", fields);
                AddFiniteNumber(request.Parameters, "camera_y", fields);
                EnsureExactProperties(request.Parameters, "move_mode", "camera_x", "camera_y");
                break;

            case EquipmentActionNames.Focus:
                AddHfw(request.Parameters, fields);
                AddPositiveFiniteNumber(request.Parameters, "range", fields);
                AddInteger(request.Parameters, "steps", fields, minimum: 4, maximum: int.MaxValue);
                EnsureExactProperties(request.Parameters, "hfw", "range", "steps");
                break;

            case EquipmentActionNames.Integration:
                AddHfw(request.Parameters, fields);
                AddFrameCount(request.Parameters, fields, live: false);
                AddImagePath(request.Parameters, fields);
                EnsureExactProperties(request.Parameters, "hfw", "frame_count", "image_path");
                break;

            case EquipmentActionNames.Live:
                AddHfw(request.Parameters, fields);
                AddFrameCount(request.Parameters, fields, live: true);
                AddImagePath(request.Parameters, fields);
                EnsureExactProperties(request.Parameters, "hfw", "frame_count", "image_path");
                break;

            case EquipmentActionNames.Abort:
                EnsureExactProperties(request.Parameters);
                break;

            default:
                throw new InvalidDataException($"Unsupported request action '{request.Action}'.");
        }

        return fields;
    }

    private static Dictionary<string, string> CreateResponseFields(EquipmentResponseMessage response)
    {
        var fields = CreateEnvelopeFields(response.Type, response.CorrelationId, response.Action);
        fields.Add("result", response.Result.ToString(CultureInfo.InvariantCulture));

        switch (response.Action)
        {
            case EquipmentActionNames.Stage:
                AddFiniteNumber(response.Properties, "current_stage_x", fields);
                AddFiniteNumber(response.Properties, "current_stage_y", fields);
                EnsureExactProperties(response.Properties, "current_stage_x", "current_stage_y");
                break;

            case EquipmentActionNames.Camera:
                AddFiniteNumber(response.Properties, "current_camera_x", fields);
                AddFiniteNumber(response.Properties, "current_camera_y", fields);
                EnsureExactProperties(response.Properties, "current_camera_x", "current_camera_y");
                break;

            case EquipmentActionNames.Focus:
                fields.Add(
                    "z_to_sharpness_2d",
                    SerializeFocusMatrix(GetFocusMatrix(response.Properties, "z_to_sharpness_2d")));
                EnsureExactProperties(response.Properties, "z_to_sharpness_2d");
                break;

            case EquipmentActionNames.Integration:
                AddHfw(response.Properties, fields);
                AddFrameCount(response.Properties, fields, live: false);
                AddImagePath(response.Properties, fields);
                EnsureExactProperties(response.Properties, "hfw", "frame_count", "image_path");
                break;

            case EquipmentActionNames.Live:
                AddHfw(response.Properties, fields);
                AddFrameCount(response.Properties, fields, live: true);
                AddImagePath(response.Properties, fields);
                EnsureExactProperties(response.Properties, "hfw", "frame_count", "image_path");
                break;

            case EquipmentActionNames.Abort:
                EnsureExactProperties(response.Properties);
                break;

            default:
                throw new InvalidDataException($"Unsupported response action '{response.Action}'.");
        }

        return fields;
    }

    private static bool TryCreateRequest(
        string templateAction,
        IReadOnlyDictionary<string, string> fields,
        out EquipmentRequestMessage? request)
    {
        request = null;
        try
        {
            if (!TryReadCommonEnvelope(fields, "request", templateAction, out var correlationId))
            {
                return false;
            }

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            switch (templateAction)
            {
                case EquipmentActionNames.Stage:
                    if (!TryReadMoveMode(fields, "move_mode", out var stageMode)
                        || !TryReadFinite(fields, "stage_x", out var stageX)
                        || !TryReadFinite(fields, "stage_y", out var stageY))
                    {
                        return false;
                    }

                    parameters["move_mode"] = stageMode;
                    parameters["stage_x"] = stageX;
                    parameters["stage_y"] = stageY;
                    break;

                case EquipmentActionNames.Camera:
                    if (!TryReadMoveMode(fields, "move_mode", out var cameraMode)
                        || !TryReadFinite(fields, "camera_x", out var cameraX)
                        || !TryReadFinite(fields, "camera_y", out var cameraY))
                    {
                        return false;
                    }

                    parameters["move_mode"] = cameraMode;
                    parameters["camera_x"] = cameraX;
                    parameters["camera_y"] = cameraY;
                    break;

                case EquipmentActionNames.Focus:
                    if (!TryReadHfw(fields, out var focusHfw)
                        || !TryReadPositiveFinite(fields, "range", out var range)
                        || !TryReadInteger(fields, "steps", 4, int.MaxValue, out var steps))
                    {
                        return false;
                    }

                    parameters["hfw"] = focusHfw;
                    parameters["range"] = range;
                    parameters["steps"] = steps;
                    break;

                case EquipmentActionNames.Integration:
                case EquipmentActionNames.Live:
                    var isLive = string.Equals(templateAction, EquipmentActionNames.Live, StringComparison.Ordinal);
                    if (!TryReadHfw(fields, out var imageHfw)
                        || !TryReadFrameCount(fields, isLive, out var frameCount)
                        || !TryReadImagePath(fields, out var requestImagePath))
                    {
                        return false;
                    }

                    parameters["hfw"] = imageHfw;
                    parameters["frame_count"] = frameCount;
                    parameters["image_path"] = requestImagePath;
                    break;
            }

            request = new EquipmentRequestMessage(correlationId, templateAction, parameters);
            return true;
        }
        catch (Exception exception) when (IsLogicalFormatFailure(exception))
        {
            request = null;
            return false;
        }
    }

    private static bool TryCreateResponse(
        string templateAction,
        IReadOnlyDictionary<string, string> fields,
        out EquipmentResponseMessage? response)
    {
        response = null;
        try
        {
            if (!TryReadCommonEnvelope(fields, "response", templateAction, out var correlationId)
                || !TryReadInteger(fields, "result", 0, 1, out var result))
            {
                return false;
            }

            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            switch (templateAction)
            {
                case EquipmentActionNames.Stage:
                    if (!TryReadFinite(fields, "current_stage_x", out var stageX)
                        || !TryReadFinite(fields, "current_stage_y", out var stageY))
                    {
                        return false;
                    }

                    properties["current_stage_x"] = stageX;
                    properties["current_stage_y"] = stageY;
                    break;

                case EquipmentActionNames.Camera:
                    if (!TryReadFinite(fields, "current_camera_x", out var cameraX)
                        || !TryReadFinite(fields, "current_camera_y", out var cameraY))
                    {
                        return false;
                    }

                    properties["current_camera_x"] = cameraX;
                    properties["current_camera_y"] = cameraY;
                    break;

                case EquipmentActionNames.Focus:
                    if (!TryParseFocusMatrix(fields["z_to_sharpness_2d"], out var matrix))
                    {
                        return false;
                    }

                    properties["z_to_sharpness_2d"] = matrix;
                    break;

                case EquipmentActionNames.Integration:
                case EquipmentActionNames.Live:
                    var isLive = string.Equals(templateAction, EquipmentActionNames.Live, StringComparison.Ordinal);
                    if (!TryReadHfw(fields, out var hfw)
                        || !TryReadFrameCount(fields, isLive, out var frameCount)
                        || !TryReadImagePath(fields, out var imagePath))
                    {
                        return false;
                    }

                    properties["hfw"] = hfw;
                    properties["frame_count"] = frameCount;
                    properties["image_path"] = imagePath;
                    break;
            }

            response = new EquipmentResponseMessage(
                correlationId,
                templateAction,
                result,
                properties);
            return true;
        }
        catch (Exception exception) when (IsLogicalFormatFailure(exception))
        {
            response = null;
            return false;
        }
    }

    private static Dictionary<string, string> CreateEnvelopeFields(
        string type,
        int correlationId,
        string action)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["correlation_id"] = correlationId.ToString(CultureInfo.InvariantCulture),
            ["action"] = action
        };
    }

    private static void AddMoveMode(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        IDictionary<string, string> fields)
    {
        if (!TryGetString(properties, name, out var mode)
            || !(string.Equals(mode, "relative", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(mode, "absolute", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"'{name}' must be 'relative' or 'absolute'.");
        }

        fields.Add(name, mode.ToLowerInvariant());
    }

    private static void AddHfw(
        IReadOnlyDictionary<string, object?> properties,
        IDictionary<string, string> fields)
    {
        if (!TryGetFiniteNumber(properties, "hfw", out var hfw)
            || hfw <= 0d
            || hfw >= MaximumHfwMetres)
        {
            throw new InvalidDataException("'hfw' must be greater than 0 m and less than 2.4E-3 m.");
        }

        fields.Add("hfw", FormatScientific(hfw));
    }

    private static void AddFiniteNumber(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        IDictionary<string, string> fields)
    {
        if (!TryGetFiniteNumber(properties, name, out var value))
        {
            throw new InvalidDataException($"'{name}' must be a finite number.");
        }

        fields.Add(name, FormatScientific(value));
    }

    private static void AddPositiveFiniteNumber(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        IDictionary<string, string> fields)
    {
        if (!TryGetFiniteNumber(properties, name, out var value) || value <= 0d)
        {
            throw new InvalidDataException($"'{name}' must be a finite number greater than zero.");
        }

        fields.Add(name, FormatScientific(value));
    }

    private static void AddInteger(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        IDictionary<string, string> fields,
        int minimum,
        int maximum)
    {
        if (!TryGetInteger(properties, name, out var value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"'{name}' must be an integer from {minimum} through {maximum}.");
        }

        fields.Add(name, value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddFrameCount(
        IReadOnlyDictionary<string, object?> properties,
        IDictionary<string, string> fields,
        bool live)
    {
        if (!TryGetInteger(properties, "frame_count", out var frameCount)
            || live && frameCount != 1
            || !live && (!IsPowerOfTwo(frameCount) || frameCount > MaximumIntegrationFrameCount))
        {
            throw new InvalidDataException(
                live
                    ? "A live 'frame_count' must be exactly 1."
                    : "An integration 'frame_count' must be a power of two from 1 through 64.");
        }

        fields.Add("frame_count", frameCount.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddImagePath(
        IReadOnlyDictionary<string, object?> properties,
        IDictionary<string, string> fields)
    {
        if (!TryGetString(properties, "image_path", out var path)
            || !EquipmentResponseMessage.IsSupportedAbsoluteImagePath(path))
        {
            throw new InvalidDataException(
                "'image_path' must be an absolute local-drive or UNC file path.");
        }

        fields.Add("image_path", path);
    }

    private static IReadOnlyList<IReadOnlyList<double>>? GetFocusMatrix(
        IReadOnlyDictionary<string, object?> properties,
        string name)
    {
        if (!properties.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (!(value is IEnumerable rows) || value is string)
        {
            throw new InvalidDataException(
                "'z_to_sharpness_2d' must be null or an array of [z, sharpness] pairs.");
        }

        var matrix = new List<IReadOnlyList<double>>();
        foreach (var row in rows)
        {
            if (!(row is IEnumerable values) || row is string)
            {
                throw new InvalidDataException(
                    "Each z_to_sharpness_2d row must contain two numbers.");
            }

            var pair = new List<double>();
            foreach (var valueItem in values)
            {
                if (!TryConvertFiniteNumber(valueItem, out var number) || number <= 0d)
                {
                    throw new InvalidDataException(
                        "Each z_to_sharpness_2d value must be finite and greater than zero.");
                }

                pair.Add(number);
            }

            if (pair.Count != 2)
            {
                throw new InvalidDataException(
                    "Each z_to_sharpness_2d row must contain exactly two numbers.");
            }

            matrix.Add(Array.AsReadOnly(pair.ToArray()));
        }

        return matrix.AsReadOnly();
    }

    private static string SerializeFocusMatrix(IReadOnlyList<IReadOnlyList<double>>? matrix)
    {
        if (matrix is null)
        {
            return "null";
        }

        return "[" + string.Join(
                   ",",
                   matrix.Select(pair =>
                       "[" + FormatScientific(pair[0]) + "," + FormatScientific(pair[1]) + "]"))
               + "]";
    }

    private static bool TryParseFocusMatrix(
        string text,
        out IReadOnlyList<IReadOnlyList<double>>? matrix)
    {
        matrix = null;
        var cursor = 0;
        SkipJsonWhitespace(text, ref cursor);
        if (cursor + 4 <= text.Length
            && string.Equals(text.Substring(cursor, 4), "null", StringComparison.Ordinal))
        {
            cursor += 4;
            SkipJsonWhitespace(text, ref cursor);
            return cursor == text.Length;
        }

        if (!Consume(text, ref cursor, '['))
        {
            return false;
        }

        SkipJsonWhitespace(text, ref cursor);
        var rows = new List<IReadOnlyList<double>>();
        if (Consume(text, ref cursor, ']'))
        {
            SkipJsonWhitespace(text, ref cursor);
            matrix = rows.AsReadOnly();
            return cursor == text.Length;
        }

        while (true)
        {
            if (!Consume(text, ref cursor, '['))
            {
                return false;
            }

            SkipJsonWhitespace(text, ref cursor);
            if (!TryReadCompactNumber(text, ref cursor, out var z) || z <= 0d)
            {
                return false;
            }

            SkipJsonWhitespace(text, ref cursor);
            if (!Consume(text, ref cursor, ','))
            {
                return false;
            }

            SkipJsonWhitespace(text, ref cursor);
            if (!TryReadCompactNumber(text, ref cursor, out var sharpness) || sharpness <= 0d)
            {
                return false;
            }

            SkipJsonWhitespace(text, ref cursor);
            if (!Consume(text, ref cursor, ']'))
            {
                return false;
            }

            SkipJsonWhitespace(text, ref cursor);
            rows.Add(Array.AsReadOnly(new[] { z, sharpness }));
            if (Consume(text, ref cursor, ']'))
            {
                SkipJsonWhitespace(text, ref cursor);
                matrix = rows.AsReadOnly();
                return cursor == text.Length;
            }

            if (!Consume(text, ref cursor, ','))
            {
                return false;
            }

            SkipJsonWhitespace(text, ref cursor);
        }
    }

    private static bool TryReadCompactNumber(string text, ref int cursor, out double value)
    {
        value = 0d;
        var start = cursor;
        while (cursor < text.Length
               && text[cursor] != ','
               && text[cursor] != ']'
               && !IsJsonWhitespace(text[cursor]))
        {
            cursor++;
        }

        return cursor > start
               && double.TryParse(
                   text.Substring(start, cursor - start),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value)
               && !double.IsNaN(value)
               && !double.IsInfinity(value);
    }

    private static void SkipJsonWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && IsJsonWhitespace(text[cursor]))
        {
            cursor++;
        }
    }

    private static bool IsJsonWhitespace(char character)
    {
        return character == ' '
               || character == '\t'
               || character == '\r'
               || character == '\n';
    }

    private static bool Consume(string text, ref int cursor, char expected)
    {
        if (cursor >= text.Length || text[cursor] != expected)
        {
            return false;
        }

        cursor++;
        return true;
    }

    private static bool TryReadCommonEnvelope(
        IReadOnlyDictionary<string, string> fields,
        string expectedType,
        string expectedAction,
        out int correlationId)
    {
        correlationId = 0;
        return string.Equals(fields["type"], expectedType, StringComparison.Ordinal)
               && string.Equals(fields["action"], expectedAction, StringComparison.Ordinal)
               && int.TryParse(
                   fields["correlation_id"],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out correlationId)
               && correlationId > 0;
    }

    private static bool TryReadMoveMode(
        IReadOnlyDictionary<string, string> fields,
        string name,
        out string mode)
    {
        mode = fields[name];
        return string.Equals(mode, "relative", StringComparison.Ordinal)
               || string.Equals(mode, "absolute", StringComparison.Ordinal);
    }

    private static bool TryReadHfw(IReadOnlyDictionary<string, string> fields, out double hfw)
    {
        return TryReadFinite(fields, "hfw", out hfw)
               && hfw > 0d
               && hfw < MaximumHfwMetres;
    }

    private static bool TryReadPositiveFinite(
        IReadOnlyDictionary<string, string> fields,
        string name,
        out double value)
    {
        return TryReadFinite(fields, name, out value) && value > 0d;
    }

    private static bool TryReadFinite(
        IReadOnlyDictionary<string, string> fields,
        string name,
        out double value)
    {
        return double.TryParse(
                   fields[name],
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value)
               && !double.IsNaN(value)
               && !double.IsInfinity(value);
    }

    private static bool TryReadInteger(
        IReadOnlyDictionary<string, string> fields,
        string name,
        int minimum,
        int maximum,
        out int value)
    {
        return int.TryParse(
                   fields[name],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value)
               && value >= minimum
               && value <= maximum;
    }

    private static bool TryReadFrameCount(
        IReadOnlyDictionary<string, string> fields,
        bool live,
        out int frameCount)
    {
        if (!int.TryParse(
                fields["frame_count"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out frameCount))
        {
            return false;
        }

        return live
            ? frameCount == 1
            : IsPowerOfTwo(frameCount) && frameCount <= MaximumIntegrationFrameCount;
    }

    private static bool TryReadImagePath(
        IReadOnlyDictionary<string, string> fields,
        out string imagePath)
    {
        imagePath = fields["image_path"];
        return EquipmentResponseMessage.IsSupportedAbsoluteImagePath(imagePath);
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(properties, name, out var raw) || !(raw is string text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetFiniteNumber(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        out double value)
    {
        value = 0d;
        return TryGetProperty(properties, name, out var raw)
               && TryConvertFiniteNumber(raw, out value);
    }

    private static bool TryGetInteger(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        out int value)
    {
        value = 0;
        if (!TryGetFiniteNumber(properties, name, out var number)
            || number != Math.Truncate(number)
            || number < int.MinValue
            || number > int.MaxValue)
        {
            return false;
        }

        value = (int)number;
        return true;
    }

    private static bool TryGetProperty(
        IReadOnlyDictionary<string, object?> properties,
        string name,
        out object? value)
    {
        if (properties.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var pair in properties)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryConvertFiniteNumber(object? value, out double number)
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

    private static void EnsureExactProperties(
        IReadOnlyDictionary<string, object?> properties,
        params string[] expectedNames)
    {
        var actual = new HashSet<string>(properties.Keys, StringComparer.Ordinal);
        var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                "The logical message fields do not exactly match the selected action template.");
        }
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & value - 1) == 0;
    }

    private static string FormatScientific(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidDataException("Equipment numbers must be finite.");
        }

        var text = value.ToString("0.#################E+0", CultureInfo.InvariantCulture);
        var marker = text.IndexOf('E');
        var exponent = int.Parse(
            text.Substring(marker + 1),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);
        return text.Substring(0, marker)
               + "E"
               + (exponent > 0 ? "+" : string.Empty)
               + exponent.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryDecode(byte[]? payload, out string text)
    {
        text = string.Empty;
        if (payload is null
            || payload.Length == 0
            || payload.Length > EquipmentMessageLimits.MaximumWirePayloadBytes)
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(payload);
            return text.Length > 0 && text[0] != '\uFEFF';
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] EncodeWithinLimit(string text)
    {
        var payload = StrictUtf8.GetBytes(text);
        if (payload.Length > EquipmentMessageLimits.MaximumWirePayloadBytes)
        {
            throw new InvalidDataException(
                $"The equipment XML payload exceeds the {EquipmentMessageLimits.MaximumWirePayloadBytes} byte limit.");
        }

        return payload;
    }

    private static bool IsLogicalFormatFailure(Exception exception)
    {
        return exception is ArgumentException
               || exception is InvalidOperationException
               || exception is InvalidDataException
               || exception is FormatException
               || exception is OverflowException;
    }

    private static string LoadEmbeddedTemplate(string action, string direction)
    {
        var folder = char.ToUpperInvariant(action[0]) + action.Substring(1);
        var resourceName = typeof(XmlTemplateEquipmentMessageCodec).Namespace
                           + ".Templates."
                           + folder
                           + "."
                           + direction
                           + ".xml";
        var assembly = typeof(XmlTemplateEquipmentMessageCodec).GetTypeInfo().Assembly;
        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            if (stream is null)
            {
                throw new InvalidDataException(
                    $"Required equipment XML template '{resourceName}' is missing.");
            }

            if (stream.CanSeek
                && stream.Length > EquipmentMessageLimits.MaximumWirePayloadBytes)
            {
                throw new InvalidDataException(
                    $"Equipment XML template '{resourceName}' exceeds the "
                    + $"{EquipmentMessageLimits.MaximumWirePayloadBytes} byte limit.");
            }

            using (var reader = new StreamReader(stream, StrictUtf8, false, 4096, false))
            {
                return reader.ReadToEnd();
            }
        }
    }

    private static IReadOnlyCollection<string> GetExpectedRequestFields(string action)
    {
        switch (action)
        {
            case EquipmentActionNames.Stage:
                return Fields("type", "correlation_id", "action", "move_mode", "stage_x", "stage_y");
            case EquipmentActionNames.Camera:
                return Fields("type", "correlation_id", "action", "move_mode", "camera_x", "camera_y");
            case EquipmentActionNames.Focus:
                return Fields("type", "correlation_id", "action", "hfw", "range", "steps");
            case EquipmentActionNames.Integration:
            case EquipmentActionNames.Live:
                return Fields("type", "correlation_id", "action", "hfw", "frame_count", "image_path");
            case EquipmentActionNames.Abort:
                return Fields("type", "correlation_id", "action");
            default:
                throw new InvalidDataException($"Unknown action '{action}'.");
        }
    }

    private static IReadOnlyCollection<string> GetExpectedResponseFields(string action)
    {
        switch (action)
        {
            case EquipmentActionNames.Stage:
                return Fields("type", "correlation_id", "action", "result", "current_stage_x", "current_stage_y");
            case EquipmentActionNames.Camera:
                return Fields("type", "correlation_id", "action", "result", "current_camera_x", "current_camera_y");
            case EquipmentActionNames.Focus:
                return Fields("type", "correlation_id", "action", "result", "z_to_sharpness_2d");
            case EquipmentActionNames.Integration:
            case EquipmentActionNames.Live:
                return Fields("type", "correlation_id", "action", "result", "hfw", "frame_count", "image_path");
            case EquipmentActionNames.Abort:
                return Fields("type", "correlation_id", "action", "result");
            default:
                throw new InvalidDataException($"Unknown action '{action}'.");
        }
    }

    private static IReadOnlyCollection<string> Fields(params string[] fields)
    {
        return Array.AsReadOnly(fields);
    }

    private sealed class ActionTemplates
    {
        public ActionTemplates(TemplateDefinition request, TemplateDefinition response)
        {
            Request = request;
            Response = response;
        }

        public TemplateDefinition Request { get; }

        public TemplateDefinition Response { get; }
    }

    private sealed class TemplateDefinition
    {
        private static readonly Regex PlaceholderPattern = new Regex(
            "\\{\\{\\{([a-z][a-z0-9_]*)\\}\\}\\}",
            RegexOptions.CultureInvariant);

        private readonly IReadOnlyList<string> _placeholders;
        private readonly IReadOnlyList<string> _literals;

        private TemplateDefinition(
            IReadOnlyList<string> placeholders,
            IReadOnlyList<string> literals)
        {
            _placeholders = placeholders;
            _literals = literals;
        }

        public static TemplateDefinition Parse(
            string action,
            string direction,
            string template,
            IReadOnlyCollection<string> expectedFields)
        {
            if (string.IsNullOrEmpty(template))
            {
                throw new InvalidDataException(
                    $"The {action} {direction} XML template is empty.");
            }

            if (template.IndexOf('\uFEFF') >= 0)
            {
                throw new InvalidDataException(
                    $"The {action} {direction} XML template contains a UTF-8 BOM/U+FEFF marker; "
                    + "templates must be UTF-8 without BOM.");
            }

            if (StrictUtf8.GetByteCount(template) > EquipmentMessageLimits.MaximumWirePayloadBytes)
            {
                throw new InvalidDataException(
                    $"The {action} {direction} XML template exceeds the "
                    + $"{EquipmentMessageLimits.MaximumWirePayloadBytes} byte limit.");
            }

            var matches = PlaceholderPattern.Matches(template);
            var placeholders = new List<string>();
            var literals = new List<string>();
            var cursor = 0;
            foreach (Match match in matches)
            {
                literals.Add(template.Substring(cursor, match.Index - cursor));
                placeholders.Add(match.Groups[1].Value);
                cursor = match.Index + match.Length;
            }

            literals.Add(template.Substring(cursor));
            var actualFields = new HashSet<string>(placeholders, StringComparer.Ordinal);
            var expected = new HashSet<string>(expectedFields, StringComparer.Ordinal);
            if (placeholders.Count != actualFields.Count || !actualFields.SetEquals(expected))
            {
                throw new InvalidDataException(
                    $"The {action} {direction} template placeholders do not match its logical contract.");
            }

            for (var index = 1; index < literals.Count - 1; index++)
            {
                if (literals[index].Length == 0)
                {
                    throw new InvalidDataException(
                        $"The {action} {direction} template contains adjacent placeholders.");
                }
            }

            return new TemplateDefinition(
                placeholders.AsReadOnly(),
                literals.AsReadOnly());
        }

        public string Render(IReadOnlyDictionary<string, string> values)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < _placeholders.Count; index++)
            {
                builder.Append(_literals[index]);
                if (!values.TryGetValue(_placeholders[index], out var value))
                {
                    throw new InvalidDataException(
                        $"A value for placeholder '{_placeholders[index]}' is missing.");
                }

                builder.Append(EscapeXml(value));
            }

            builder.Append(_literals[_literals.Count - 1]);
            return builder.ToString();
        }

        public bool TryExtract(
            string text,
            out IReadOnlyDictionary<string, string> values)
        {
            values = new Dictionary<string, string>();
            if (!text.StartsWith(_literals[0], StringComparison.Ordinal))
            {
                return false;
            }

            var cursor = _literals[0].Length;
            var extracted = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < _placeholders.Count; index++)
            {
                var nextLiteral = _literals[index + 1];
                var end = nextLiteral.Length == 0
                    ? text.Length
                    : text.IndexOf(nextLiteral, cursor, StringComparison.Ordinal);
                if (end < cursor
                    || !TryUnescapeXml(text.Substring(cursor, end - cursor), out var value))
                {
                    return false;
                }

                extracted.Add(_placeholders[index], value);
                cursor = end + nextLiteral.Length;
            }

            if (cursor != text.Length)
            {
                return false;
            }

            values = new ReadOnlyDictionary<string, string>(extracted);
            return true;
        }

        private static string EscapeXml(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static bool TryUnescapeXml(string value, out string unescaped)
        {
            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '&')
                {
                    builder.Append(value[index]);
                    continue;
                }

                if (TryReadEntity(value, index, out var entityLength, out var decoded))
                {
                    builder.Append(decoded);
                    index += entityLength - 1;
                    continue;
                }

                unescaped = string.Empty;
                return false;
            }

            unescaped = builder.ToString();
            return true;
        }

        private static bool TryReadEntity(
            string text,
            int index,
            out int entityLength,
            out char decoded)
        {
            var entities = new[]
            {
                new KeyValuePair<string, char>("&amp;", '&'),
                new KeyValuePair<string, char>("&lt;", '<'),
                new KeyValuePair<string, char>("&gt;", '>'),
                new KeyValuePair<string, char>("&quot;", '"'),
                new KeyValuePair<string, char>("&apos;", '\'')
            };
            foreach (var entity in entities)
            {
                if (index + entity.Key.Length <= text.Length
                    && string.Equals(
                        text.Substring(index, entity.Key.Length),
                        entity.Key,
                        StringComparison.Ordinal))
                {
                    entityLength = entity.Key.Length;
                    decoded = entity.Value;
                    return true;
                }
            }

            entityLength = 0;
            decoded = default;
            return false;
        }
    }
}
