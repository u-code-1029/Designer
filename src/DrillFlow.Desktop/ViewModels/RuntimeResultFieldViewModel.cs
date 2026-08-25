using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json;

namespace DrillFlow.Desktop.ViewModels;

public sealed class RuntimeResultFieldViewModel : ObservableObject
{
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
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Label));
        };
    }

    public string Name { get; }

    public string ExpressionPath => _actionAlias + ".result." + Name;

    public string Value { get; }

    public string Description => _localization[Name switch
    {
        "command" => "ResultCommandDescription",
        "drill_result_path" => "ParamResultPath",
        "position_x" => "ResultPositionXDescription",
        "position_y" => "ResultPositionYDescription",
        "measured_distance" => "ResultMeasuredDistanceDescription",
        "request_json" => "RequestJson",
        "response_json" => "ResponseJson",
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
}
