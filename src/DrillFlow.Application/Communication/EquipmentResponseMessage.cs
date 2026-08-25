using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DrillFlow.Application.Communication;

public sealed class EquipmentResponseMessage
{
    public EquipmentResponseMessage(
        int index,
        string command,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        Index = index;
        Command = command ?? throw new ArgumentNullException(nameof(command));
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (var property in properties)
            {
                copy.Add(property.Key, property.Value);
            }
        }

        Properties = new ReadOnlyDictionary<string, object?>(copy);
    }

    public int Index { get; }

    public string Command { get; }

    /// <summary>
    /// All response properties except the correlation index and command.
    /// Unknown properties are intentionally retained for expressions and future protocol growth.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }
}
