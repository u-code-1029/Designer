using System;

namespace DrillFlow.Application.Communication;

/// <summary>Combines a configured Windows image directory with one leaf filename.</summary>
public static class EquipmentImagePath
{
    public static string Create(string? directory, string fileName)
    {
        var normalizedDirectory = EquipmentCommunicationOptions.NormalizeExchangeDirectory(directory);
        if (normalizedDirectory.Length == 0)
        {
            throw new ArgumentException("An image output directory is required.", nameof(directory));
        }

        if (string.IsNullOrWhiteSpace(fileName)
            || fileName == "."
            || fileName == ".."
            || fileName.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
        {
            throw new ArgumentException("The image filename must be a Windows leaf filename.", nameof(fileName));
        }

        foreach (var character in fileName)
        {
            if (character < ' ')
            {
                throw new ArgumentException("The image filename cannot contain control characters.", nameof(fileName));
            }
        }

        // Wire image paths always use Windows separators, including when authoring or validating
        // a workflow on another platform. A drive root keeps its separator after this join.
        return normalizedDirectory.TrimEnd('\\') + "\\" + fileName;
    }
}
