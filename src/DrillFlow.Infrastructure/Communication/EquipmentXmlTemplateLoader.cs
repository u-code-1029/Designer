using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using DrillFlow.Application.Communication;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// Captures a complete external template set once at startup. A present external directory is
/// authoritative: incomplete or unreadable replacements must never silently use embedded XML.
/// </summary>
internal static class EquipmentXmlTemplateLoader
{
    public static Func<string, string, string> Create(
        string directory,
        bool allowMissingDirectory,
        Func<string, string, string> embeddedLoader,
        Func<byte[], string, string> decodeTemplateBytes)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("An equipment XML template directory is required.", nameof(directory));
        }

        var fullDirectory = Path.GetFullPath(directory);
        if (!IsAvailableDirectory(fullDirectory, allowMissingDirectory))
        {
            return embeddedLoader;
        }

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in EquipmentActionNames.All)
        {
            var folder = char.ToUpperInvariant(action[0]) + action.Substring(1);
            foreach (var direction in GetDirections(action))
            {
                var path = Path.Combine(fullDirectory, folder, direction + ".xml");
                templates.Add(action + "/" + direction, ReadTemplate(path, decodeTemplateBytes));
            }
        }

        return (action, direction) => templates[action + "/" + direction];
    }

    private static bool IsAvailableDirectory(string directory, bool allowMissingDirectory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException(
                    $"Equipment XML template path '{directory}' is not a directory.");
            }

            return true;
        }
        catch (FileNotFoundException exception)
        {
            return HandleMissingDirectory(directory, allowMissingDirectory, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            return HandleMissingDirectory(directory, allowMissingDirectory, exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(
                $"Equipment XML template directory '{directory}' could not be accessed.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidDataException(
                $"Equipment XML template directory '{directory}' could not be accessed.", exception);
        }
    }

    private static bool HandleMissingDirectory(string directory, bool allowMissingDirectory, Exception exception)
    {
        if (allowMissingDirectory)
        {
            return false;
        }

        throw new InvalidDataException(
            $"Required equipment XML template directory '{directory}' is missing.", exception);
    }

    private static IEnumerable<string> GetDirections(string action)
    {
        yield return "request";
        yield return "response";
        if (action != EquipmentActionNames.Abort && action != EquipmentActionNames.AutoContrastBrightness)
        {
            yield return "failure-response";
        }
    }

    private static string ReadTemplate(string path, Func<byte[], string, string> decodeTemplateBytes)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var length = stream.Length;
                if (length > EquipmentMessageLimits.MaximumWirePayloadBytes)
                {
                    throw new InvalidDataException(
                        $"Equipment XML template '{path}' exceeds the "
                        + $"{EquipmentMessageLimits.MaximumWirePayloadBytes} byte limit.");
                }

                var payload = new byte[(int)length];
                var offset = 0;
                while (offset < payload.Length)
                {
                    var count = stream.Read(payload, offset, payload.Length - offset);
                    if (count == 0)
                    {
                        throw new InvalidDataException(
                            $"Equipment XML template '{path}' changed while it was being loaded.");
                    }

                    offset += count;
                }

                if (stream.Length != length || stream.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        $"Equipment XML template '{path}' changed while it was being loaded.");
                }

                var template = decodeTemplateBytes(payload, path);
                ValidateXmlDocument(template, path);
                return template;
            }
        }
        catch (IOException exception)
        {
            throw new InvalidDataException(
                $"Required equipment XML template '{path}' could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidDataException(
                $"Required equipment XML template '{path}' could not be read.", exception);
        }
    }

    private static void ValidateXmlDocument(string template, string path)
    {
        var settings = new XmlReaderSettings
        {
            CheckCharacters = true,
            ConformanceLevel = ConformanceLevel.Document,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = EquipmentMessageLimits.MaximumWirePayloadBytes
        };
        try
        {
            // The comparison contract permits outer XML formatting whitespace. Validate a view
            // with that whitespace trimmed, while keeping the actual template bytes unchanged.
            using (var input = new StringReader(template.Trim(' ', '\t', '\r', '\n')))
            using (var reader = XmlReader.Create(input, settings))
            {
                while (reader.Read())
                {
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"Equipment XML template '{path}' must be a well-formed XML document without a DTD.", exception);
        }
    }
}
