using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json;

namespace DrillFlow.Desktop.ViewModels;

public sealed class RuntimeResultFieldViewModel : ObservableObject
{
    private const int MaximumSummaryLength = 160;
    private static readonly Regex SummaryWhitespacePattern = new("\\s+", RegexOptions.CultureInvariant);
    private readonly ILocalizationService _localization;
    private string _actionAlias;

    public RuntimeResultFieldViewModel(
        string actionAlias,
        string name,
        object? value,
        ILocalizationService localization)
    {
        _localization = localization;
        _actionAlias = actionAlias ?? string.Empty;
        Name = name ?? string.Empty;
        Value = FormatValue(value);
        SummaryValue = FormatSummaryValue(Value);
    }

    public string Name { get; }

    public string ExpressionPath => _actionAlias + ".result." + Name;

    public string Value { get; }

    public string SummaryValue { get; }

    public string Description => _localization[Name switch
    {
        "type" => "ResultTypeDescription",
        "correlation_id" => "ResultCorrelationDescription",
        "action" => "ResultActionDescription",
        "result" => "ResultStatusDescription",
        "current_stage_x" => "ResultCurrentStageXDescription",
        "current_stage_y" => "ResultCurrentStageYDescription",
        "current_camera_x" => "ResultCurrentCameraXDescription",
        "current_camera_y" => "ResultCurrentCameraYDescription",
        "hfw" => "ParamHfw",
        "frame_count" => "ParamFrameCount",
        "z_to_sharpness_2d" => "ResultFocusSamplesDescription",
        "image_path" => "ResultImagePathDescription",
        "status_code" => "ResultHttpStatusDescription",
        "is_success" => "ResultHttpSuccessDescription",
        "reason_phrase" => "ResultHttpReasonDescription",
        "headers" => "ResultHttpHeadersDescription",
        "body_text" => "ResultHttpBodyDescription",
        "content_type" => "ResultHttpContentTypeDescription",
        "json" => "ResultHttpJsonDescription",
        _ => "ResultValueDescription"
    }];

    public string Label => ExpressionPath + " (" + Description + ")";

    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Label));
    }

    public void UpdateActionAlias(string actionAlias)
    {
        var normalized = actionAlias ?? string.Empty;
        if (string.Equals(_actionAlias, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _actionAlias = normalized;
        OnPropertyChanged(nameof(ExpressionPath));
        OnPropertyChanged(nameof(Label));
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return JsonConvert.SerializeObject(value, Formatting.Indented);
    }

    private static string FormatSummaryValue(string value)
    {
        var summary = SummaryWhitespacePattern.Replace(value ?? string.Empty, " ").Trim();
        return summary.Length <= MaximumSummaryLength
            ? summary
            : summary.Substring(0, MaximumSummaryLength - 1) + "\u2026";
    }
}
