using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Core.Runtime;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json;

namespace DrillFlow.Desktop.ViewModels;

public sealed class RuntimeResultViewModel : ObservableObject
{
    private const string ImagePathField = "image_path";
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
        SummaryFields = Fields
            .Where(field => !IsInternalRawField(field.Name))
            .ToArray();

        RequestJson = ReadSpecialValue(result, "request_json");
        ResponseJson = ReadSpecialValue(result, "response_json");
        ImagePath = ReadSpecialValue(result, ImagePathField);
    }

    public int CorrelationId { get; }

    public string IndexExpressionPath => _actionAlias + ".result.index";

    public string IterationPathExpressionPath => _actionAlias + ".result.iteration_path";

    public string IterationPath { get; }

    public DateTime CompletedAt { get; }

    public string ValuesJson { get; }

    public IReadOnlyList<RuntimeResultFieldViewModel> Fields { get; }

    public IReadOnlyList<RuntimeResultFieldViewModel> SummaryFields { get; }

    public bool HasSummaryFields => SummaryFields.Count > 0;

    public string RequestJson { get; }

    public string ResponseJson { get; }

    public string ImagePath { get; }

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

    internal void NotifyLanguageChanged()
    {
        foreach (var field in Fields)
        {
            field.NotifyLanguageChanged();
        }
    }

    private static string ReadSpecialValue(ActionExecutionResult result, string key)
    {
        return result.Values.TryGetValue(key, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty;
    }

    private static bool IsInternalRawField(string name) =>
        string.Equals(name, "request_json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "response_json", StringComparison.OrdinalIgnoreCase);

    internal async Task<ImageSource?> LoadImageAsync(
        ILiveImageDecoder imageDecoder,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ImagePath))
        {
            return null;
        }

        try
        {
            var image = await LiveImageFileLoader.LoadAsync(
                    ImagePath,
                    imageDecoder,
                    cancellationToken)
                .ConfigureAwait(false);
            return image.ImageSource;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A response may contain a malformed or unsupported path.
            return null;
        }
    }
}
