using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DrillFlow.Application.Communication;

/// <summary>
/// Format-independent logical equipment request. The XML template codec owns the wire
/// representation; this object deliberately mirrors the JSON shape used by the designer.
/// </summary>
public sealed class EquipmentRequestMessage
{
    public EquipmentRequestMessage(
        int correlationId,
        string action,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (correlationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(correlationId),
                "A correlation ID must be positive.");
        }

        Action = EquipmentActionNames.Normalize(action);
        CorrelationId = correlationId;
        Parameters = new ReadOnlyDictionary<string, object?>(
            CopyProperties(parameters, nameof(parameters)));
    }

    public string Type => "request";

    public int CorrelationId { get; }

    public string Action { get; }

    public IReadOnlyDictionary<string, object?> Parameters { get; }

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

        foreach (var pair in properties)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException("Parameter names cannot be empty.", argumentName);
            }

            if (!names.Add(pair.Key))
            {
                throw new ArgumentException(
                    $"Request parameter '{pair.Key}' is duplicated when casing is ignored.",
                    argumentName);
            }

            if (IsReservedProperty(pair.Key))
            {
                throw new ArgumentException(
                    $"'{pair.Key}' is a reserved request property.",
                    argumentName);
            }

            copy.Add(pair.Key, pair.Value);
        }

        return copy;
    }

    private static bool IsReservedProperty(string propertyName)
    {
        return string.Equals(propertyName, "type", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "correlation_id", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "action", StringComparison.OrdinalIgnoreCase);
    }
}

public static class EquipmentActionNames
{
    public const string Stage = "stage";
    public const string Camera = "camera";
    public const string Focus = "focus";
    public const string Integration = "integration";
    public const string Live = "live";
    public const string Om = "om";
    public const string Lens = "lens";
    public const string AutoContrastBrightness = "acb";
    public const string Abort = "abort";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        new[]
        {
            Stage,
            Camera,
            Focus,
            Integration,
            Live,
            Om,
            Lens,
            AutoContrastBrightness,
            Abort
        });

    public static bool IsKnown(string? action)
    {
        return string.Equals(action, Stage, StringComparison.Ordinal)
               || string.Equals(action, Camera, StringComparison.Ordinal)
               || string.Equals(action, Focus, StringComparison.Ordinal)
               || string.Equals(action, Integration, StringComparison.Ordinal)
               || string.Equals(action, Live, StringComparison.Ordinal)
               || string.Equals(action, Om, StringComparison.Ordinal)
               || string.Equals(action, Lens, StringComparison.Ordinal)
               || string.Equals(action, AutoContrastBrightness, StringComparison.Ordinal)
               || string.Equals(action, Abort, StringComparison.Ordinal);
    }

    public static string Normalize(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("An equipment action is required.", nameof(action));
        }

        var normalized = action!.Trim().ToLowerInvariant();
        if (!IsKnown(normalized))
        {
            throw new ArgumentException(
                $"Unsupported equipment action '{action}'.",
                nameof(action));
        }

        return normalized;
    }
}
