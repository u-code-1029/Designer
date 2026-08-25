using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DrillFlow.Application.Communication;

public sealed class EquipmentRequestMessage
{
    public EquipmentRequestMessage(
        int index,
        string command,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (index <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "A correlation index must be positive.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A command is required.", nameof(command));
        }

        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var pair in parameters)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException("Parameter names cannot be empty.", nameof(parameters));
                }

                if (string.Equals(pair.Key, "index", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "command", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"'{pair.Key}' is a reserved request property.",
                        nameof(parameters));
                }

                copy.Add(pair.Key, pair.Value);
            }
        }

        Index = index;
        Command = command;
        Parameters = new ReadOnlyDictionary<string, object?>(copy);
    }

    public int Index { get; }

    public string Command { get; }

    public IReadOnlyDictionary<string, object?> Parameters { get; }
}

