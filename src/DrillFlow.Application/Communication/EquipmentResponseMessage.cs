using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using DrillFlow.Core.Validation;

namespace DrillFlow.Application.Communication;

/// <summary>
/// Format-independent logical equipment response. Action-specific field validation is performed
/// by the equipment message codec before an instance is returned by the file transport.
/// </summary>
public sealed class EquipmentResponseMessage
{
    public EquipmentResponseMessage(
        int correlationId,
        string action,
        int result,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (correlationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(correlationId),
                "A response correlation ID must be positive.");
        }

        if (result != 0 && result != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(result),
                "An equipment response result must be 0 (success) or 1 (failure).");
        }

        Action = EquipmentActionNames.Normalize(action);
        CorrelationId = correlationId;
        Result = result;
        Properties = new ReadOnlyDictionary<string, object?>(
            CopyProperties(properties, nameof(properties)));
        _ = ImagePath;
    }

    public string Type => "response";

    public int CorrelationId { get; }

    public string Action { get; }

    public int Result { get; }

    public bool IsSuccess => Result == 0;

    public IReadOnlyDictionary<string, object?> Properties { get; }

    public string? ImagePath
    {
        get
        {
            if (!Properties.TryGetValue("image_path", out var value))
            {
                return null;
            }

            if (value is string path && IsSupportedAbsoluteImagePath(path))
            {
                return path;
            }

            throw new InvalidOperationException(
                "The equipment response 'image_path' property must be an absolute local or UNC path when present.");
        }
    }

    public double? CurrentStageX => ReadOptionalFiniteNumber("current_stage_x");

    public double? CurrentStageY => ReadOptionalFiniteNumber("current_stage_y");

    public double? CurrentCameraX => ReadOptionalFiniteNumber("current_camera_x");

    public double? CurrentCameraY => ReadOptionalFiniteNumber("current_camera_y");

    public double? Hfw => ReadOptionalFiniteNumber("hfw");

    public int? FrameCount => ReadOptionalInteger("frame_count");

    public IReadOnlyList<IReadOnlyList<double>>? ZToSharpness2D
    {
        get
        {
            if (!Properties.TryGetValue("z_to_sharpness_2d", out var value) || value is null)
            {
                return null;
            }

            if (value is IReadOnlyList<IReadOnlyList<double>> typed)
            {
                return typed;
            }

            if (value is IEnumerable<object?> rows)
            {
                var converted = new List<IReadOnlyList<double>>();
                foreach (var row in rows)
                {
                    if (!(row is IEnumerable<object?> values))
                    {
                        return null;
                    }

                    var pair = values.Select(TryConvertNumber).ToArray();
                    if (pair.Length != 2 || pair.Any(item => !item.HasValue))
                    {
                        return null;
                    }

                    converted.Add(Array.AsReadOnly(new[] { pair[0]!.Value, pair[1]!.Value }));
                }

                return converted.AsReadOnly();
            }

            return null;
        }
    }

    public static bool IsSupportedAbsoluteImagePath(string? path)
    {
        return ParameterValueValidator.IsSupportedAbsoluteWindowsFilePath(path);
    }

    private double? ReadOptionalFiniteNumber(string name)
    {
        if (!Properties.TryGetValue(name, out var value))
        {
            return null;
        }

        var number = TryConvertNumber(value);
        return number.HasValue && !double.IsNaN(number.Value) && !double.IsInfinity(number.Value)
            ? number
            : null;
    }

    private int? ReadOptionalInteger(string name)
    {
        var value = ReadOptionalFiniteNumber(name);
        return value.HasValue
               && value.Value == Math.Truncate(value.Value)
               && value.Value >= int.MinValue
               && value.Value <= int.MaxValue
            ? (int)value.Value
            : null;
    }

    private static double? TryConvertNumber(object? value)
    {
        try
        {
            switch (value)
            {
                case byte item: return item;
                case sbyte item: return item;
                case short item: return item;
                case ushort item: return item;
                case int item: return item;
                case uint item: return item;
                case long item: return item;
                case ulong item: return item;
                case float item: return item;
                case double item: return item;
                case decimal item: return Convert.ToDouble(item, CultureInfo.InvariantCulture);
                default: return null;
            }
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> CopyProperties(
        IReadOnlyDictionary<string, object?>? properties,
        string argumentName)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (properties is null)
        {
            return copy;
        }

        foreach (var property in properties)
        {
            if (string.IsNullOrWhiteSpace(property.Key))
            {
                throw new ArgumentException("Response property names cannot be empty.", argumentName);
            }

            if (!names.Add(property.Key))
            {
                throw new ArgumentException(
                    $"Response property '{property.Key}' is duplicated when casing is ignored.",
                    argumentName);
            }

            if (IsReservedProperty(property.Key))
            {
                throw new ArgumentException(
                    $"Response property '{property.Key}' conflicts with response metadata.",
                    argumentName);
            }

            copy.Add(property.Key, property.Value);
        }

        return copy;
    }

    private static bool IsReservedProperty(string propertyName)
    {
        return string.Equals(propertyName, "type", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "correlation_id", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "action", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "result", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "iteration_path", StringComparison.OrdinalIgnoreCase);
    }

}
