using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Core.Runtime;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json;

namespace DrillFlow.Desktop.ViewModels;

public sealed class RuntimeResultViewModel : ObservableObject
{
    private string _actionAlias;

    public RuntimeResultViewModel(
        ActionExecutionResult result,
        string actionAlias,
        ILocalizationService localization)
    {
        _actionAlias = actionAlias ?? string.Empty;
        CorrelationId = result.CorrelationId;
        IterationPath = result.IterationPath is { Count: > 0 }
            ? string.Join(".", result.IterationPath.Select(index => (index + 1).ToString()))
            : "-";
        CompletedAt = result.CompletedAtUtc.LocalDateTime;
        ValuesJson = JsonConvert.SerializeObject(result.Values, Formatting.Indented);
        Fields = result.Values
            .Select(pair => new RuntimeResultFieldViewModel(
                _actionAlias,
                pair.Key,
                pair.Value,
                localization))
            .ToArray();

        RequestJson = ReadSpecialValue(result, "request_json");
        ResponseJson = ReadSpecialValue(result, "response_json");
    }

    public int CorrelationId { get; }

    public string IndexExpressionPath => _actionAlias + ".result.index";

    public string IterationPathExpressionPath => _actionAlias + ".result.iteration_path";

    public string IterationPath { get; }

    public DateTime CompletedAt { get; }

    public string ValuesJson { get; }

    public IReadOnlyList<RuntimeResultFieldViewModel> Fields { get; }

    public string RequestJson { get; }

    public string ResponseJson { get; }

    public void UpdateActionAlias(string actionAlias)
    {
        var normalized = actionAlias ?? string.Empty;
        if (string.Equals(_actionAlias, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _actionAlias = normalized;
        OnPropertyChanged(nameof(IndexExpressionPath));
        OnPropertyChanged(nameof(IterationPathExpressionPath));
        foreach (var field in Fields)
        {
            field.UpdateActionAlias(normalized);
        }
    }

    private static string ReadSpecialValue(ActionExecutionResult result, string key)
    {
        return result.Values.TryGetValue(key, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty;
    }
}
