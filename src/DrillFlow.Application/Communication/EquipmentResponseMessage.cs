using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DrillFlow.Application.Communication;

public sealed class EquipmentResponseMessage
{
    public EquipmentResponseMessage(
        int index,
        string command,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (index <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "A response correlation index must be positive.");
        }

        if (!string.Equals(command, "return", StringComparison.Ordinal))
        {
            throw new ArgumentException("A response command must be exactly 'return'.", nameof(command));
        }

        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (properties is not null)
        {
            foreach (var property in properties)
            {
                if (string.IsNullOrWhiteSpace(property.Key))
                {
                    throw new ArgumentException("Response property names cannot be empty.", nameof(properties));
                }

                if (!propertyNames.Add(property.Key))
                {
                    throw new ArgumentException(
                        $"Response property '{property.Key}' duplicates another property when casing is ignored.",
                        nameof(properties));
                }

                if (IsReservedRuntimeProperty(property.Key))
                {
                    throw new ArgumentException(
                        $"Response property '{property.Key}' conflicts with runtime response metadata.",
                        nameof(properties));
                }

                if (IsNonCanonicalKnownProperty(property.Key))
                {
                    throw new ArgumentException(
                        $"Known response property '{property.Key}' must use its canonical lowercase name.",
                        nameof(properties));
                }

                copy.Add(property.Key, property.Value);
            }
        }

        Index = index;
        Command = command;
        Properties = new ReadOnlyDictionary<string, object?>(copy);

        // Keep the logical Application contract valid even when a transport test double or a
        // future codec constructs the message directly instead of using the JSON parser.
        _ = StageX;
        _ = StageY;
        _ = ImagePath;
    }

    public int Index { get; }

    public string Command { get; }

    /// <summary>The stage's absolute X coordinate in meters.</summary>
    public double StageX => ReadRequiredFiniteCoordinate("stage_x");

    /// <summary>The stage's absolute Y coordinate in meters.</summary>
    public double StageY => ReadRequiredFiniteCoordinate("stage_y");

    /// <summary>The saved result image pathname when the equipment produced an image.</summary>
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

    /// <summary>
    /// All response properties except the correlation index and command.
    /// Unknown properties are intentionally retained for expressions and future protocol growth.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }

    /// <summary>
    /// Returns whether a path uses the supported Windows absolute form: a drive-rooted pathname
    /// such as <c>C:\images\result.png</c>, or a UNC pathname below a server share.
    /// This lexical check deliberately avoids path normalization APIs that can throw for malformed
    /// controller input on older Windows versions.
    /// </summary>
    public static bool IsSupportedAbsoluteImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path![path.Length - 1] == '\\')
        {
            return false;
        }

        var driveRooted = path.Length > 3
                          && IsAsciiLetter(path[0])
                          && path[1] == ':'
                          && path[2] == '\\';
        var uncRooted = false;
        if (path.Length > 5 && path[0] == '\\' && path[1] == '\\' && path[2] != '\\')
        {
            var serverEnd = path.IndexOf('\\', 2);
            if (serverEnd > 2)
            {
                var shareEnd = path.IndexOf('\\', serverEnd + 1);
                uncRooted = shareEnd > serverEnd + 1 && shareEnd < path.Length - 1;
            }
        }

        if (!driveRooted && !uncRooted)
        {
            return false;
        }

        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character < ' '
                || character == '"'
                || character == '<'
                || character == '>'
                || character == '|'
                || character == '?'
                || character == '*'
                || character == '/')
            {
                return false;
            }

            if (character == ':' && (!driveRooted || index != 1))
            {
                return false;
            }
        }

        return true;
    }

    private double ReadRequiredFiniteCoordinate(string propertyName)
    {
        if (!Properties.TryGetValue(propertyName, out var value)
            || !TryConvertNumber(value, out var coordinate)
            || double.IsNaN(coordinate)
            || double.IsInfinity(coordinate))
        {
            throw new InvalidOperationException(
                $"The equipment response '{propertyName}' property must be a finite number in meters.");
        }

        return coordinate;
    }

    private static bool TryConvertNumber(object? value, out double number)
    {
        switch (value)
        {
            case byte item:
                number = item;
                return true;
            case sbyte item:
                number = item;
                return true;
            case short item:
                number = item;
                return true;
            case ushort item:
                number = item;
                return true;
            case int item:
                number = item;
                return true;
            case uint item:
                number = item;
                return true;
            case long item:
                number = item;
                return true;
            case ulong item:
                number = item;
                return true;
            case float item:
                number = item;
                return true;
            case double item:
                number = item;
                return true;
            case decimal item:
                number = Convert.ToDouble(item, CultureInfo.InvariantCulture);
                return true;
            default:
                number = 0d;
                return false;
        }
    }

    private static bool IsReservedRuntimeProperty(string propertyName)
    {
        return string.Equals(propertyName, "index", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "command", StringComparison.OrdinalIgnoreCase)
               || string.Equals(propertyName, "iteration_path", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonCanonicalKnownProperty(string propertyName)
    {
        return IsNonCanonical(propertyName, "stage_x")
               || IsNonCanonical(propertyName, "stage_y")
               || IsNonCanonical(propertyName, "image_path");
    }

    private static bool IsNonCanonical(string propertyName, string canonicalName)
    {
        return string.Equals(propertyName, canonicalName, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(propertyName, canonicalName, StringComparison.Ordinal);
    }

    private static bool IsAsciiLetter(char value)
    {
        return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
    }
}
