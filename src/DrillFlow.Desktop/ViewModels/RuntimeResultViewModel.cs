using System;
using System.Linq;
using DrillFlow.Core.Runtime;
using Newtonsoft.Json;

namespace DrillFlow.Desktop.ViewModels;

public sealed class RuntimeResultViewModel
{
    public RuntimeResultViewModel(ActionExecutionResult result)
    {
        CorrelationId = result.CorrelationId;
        IterationPath = result.IterationPath is { Count: > 0 }
            ? string.Join(".", result.IterationPath.Select(index => (index + 1).ToString()))
            : "-";
        CompletedAt = result.CompletedAtUtc.LocalDateTime;
        ValuesJson = JsonConvert.SerializeObject(result.Values, Formatting.Indented);

        RequestJson = ReadSpecialValue(result, "request_json");
        ResponseJson = ReadSpecialValue(result, "response_json");
    }

    public int CorrelationId { get; }

    public string IterationPath { get; }

    public DateTime CompletedAt { get; }

    public string ValuesJson { get; }

    public string RequestJson { get; }

    public string ResponseJson { get; }

    private static string ReadSpecialValue(ActionExecutionResult result, string key)
    {
        return result.Values.TryGetValue(key, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty;
    }
}
