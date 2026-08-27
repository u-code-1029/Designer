using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json.Linq;

namespace DrillFlow.Desktop.ViewModels;

public sealed class ActionParameterViewModel : ObservableObject
{
    private readonly ParameterBinding _binding;
    private readonly WorkflowNodeKind _ownerKind;
    private readonly ILocalizationService _localization;
    private string _validationMessage = string.Empty;
    private bool _isEditingEnabled = true;

    public ActionParameterViewModel(
        string name,
        string labelKey,
        ParameterBinding binding,
        WorkflowNodeKind ownerKind,
        ILocalizationService localization)
    {
        Name = name;
        LabelKey = labelKey;
        _binding = binding;
        _ownerKind = ownerKind;
        _localization = localization;
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Label));
            Validate();
        };
    }

    public string Name { get; }

    public string LabelKey { get; }

    public string Description => _localization[LabelKey];

    public string Label => Name + " (" + Description + ")";

    public string Value
    {
        get => _binding.RawText;
        set
        {
            if (!IsEditingEnabled)
            {
                return;
            }

            if (string.Equals(_binding.RawText, value, StringComparison.Ordinal))
            {
                return;
            }

            _binding.RawText = value ?? string.Empty;
            OnPropertyChanged();
            Validate();
        }
    }

    public bool IsExpression => _binding.IsExpression;

    public bool IsEditingEnabled
    {
        get => _isEditingEnabled;
        private set => SetProperty(ref _isEditingEnabled, value);
    }

    public bool HasError => !string.IsNullOrEmpty(ValidationMessage);

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool Validate()
    {
        OnPropertyChanged(nameof(IsExpression));
        var raw = (_binding.RawText ?? string.Empty).Trim();

        if (_binding.IsExpression)
        {
            ValidationMessage = string.IsNullOrWhiteSpace(_binding.ExpressionText)
                ? Localized("'=' 뒤에 Expression을 입력하세요.", "Enter an expression after '='.")
                : string.Empty;
            return !HasError;
        }

        switch (Name)
        {
            case "move_mode":
                ValidationMessage = string.Equals(raw, "relative", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(raw, "absolute", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : Localized("relative 또는 absolute를 입력하세요.", "Enter relative or absolute.");
                break;

            case "stage_x":
            case "stage_y":
            case "camera_x":
            case "camera_y":
                ValidationMessage = TryDouble(raw, out _)
                    ? string.Empty
                    : Localized(
                        "유한한 숫자를 입력하세요. 양수와 음수를 모두 사용할 수 있습니다.",
                        "Enter a finite number. Positive and negative values are allowed.");
                break;

            case "hfw":
                ValidationMessage = TryDouble(raw, out var hfw) && hfw > 0 && hfw < 2.4E-3
                    ? string.Empty
                    : Localized(
                        "0m보다 크고 2.4mm보다 작은 값을 입력하세요.",
                        "Enter a value greater than 0m and less than 2.4mm.");
                break;

            case "range":
                ValidationMessage = TryDouble(raw, out var range) && range > 0
                    ? string.Empty
                    : Localized("0m보다 큰 유한한 값을 입력하세요.", "Enter a finite value greater than 0m.");
                break;

            case "steps":
                ValidationMessage = int.TryParse(
                        raw,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var steps)
                    && steps >= 4
                        ? string.Empty
                        : Localized(
                            "4 이상 Int32 최대값 이하의 정수를 입력하세요.",
                            "Enter an integer from 4 through Int32.MaxValue.");
                break;

            case "frame_count":
                var hasIntegerFrameCount = int.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var frameCount);
                if (_ownerKind == WorkflowNodeKind.Live)
                {
                    ValidationMessage = hasIntegerFrameCount && frameCount == 1
                        ? string.Empty
                        : Localized("Live frame_count는 1로 고정됩니다.", "Live frame_count must be exactly 1.");
                }
                else
                {
                    ValidationMessage = hasIntegerFrameCount
                                        && frameCount >= 1
                                        && frameCount <= 64
                                        && (frameCount & (frameCount - 1)) == 0
                        ? string.Empty
                        : Localized(
                            "1~64 범위의 2의 거듭제곱을 입력하세요.",
                            "Enter a power of two from 1 through 64.");
                }

                break;

            case "image_path":
                ValidationMessage = EquipmentResponseMessage.IsSupportedAbsoluteImagePath(raw)
                    ? string.Empty
                    : Localized(
                        "파일명을 포함한 절대 로컬 또는 UNC 경로를 입력하세요.",
                        "Enter an absolute local or UNC path including the file name.");
                break;

            case "milliseconds":
                ValidationMessage = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay)
                    && delay >= 0
                    && delay < 30000
                        ? string.Empty
                        : Localized("0 이상 30,000 미만의 정수를 입력하세요.", "Enter an integer from 0 through 29,999.");
                break;

            case "count":
                ValidationMessage = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                    && count > 0
                        ? string.Empty
                        : Localized("1 이상의 정수를 입력하세요.", "Enter an integer greater than or equal to 1.");
                break;

            case "method":
                ValidationMessage = string.Equals(raw, "GET", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(raw, "POST", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : Localized("GET 또는 POST를 입력하세요.", "Enter GET or POST.");
                break;

            case "url":
                ValidationMessage = IsAbsoluteHttpUrl(raw)
                    ? string.Empty
                    : Localized(
                        "http 또는 https 절대 URL을 입력하세요.",
                        "Enter an absolute http or https URL.");
                break;

            case "headers":
                ValidationMessage = IsJsonObjectOrEmpty(raw)
                    ? string.Empty
                    : Localized(
                        "헤더를 JSON 객체로 입력하세요. 예: {\"Accept\":\"application/json\"}",
                        "Enter headers as a JSON object, for example {\"Accept\":\"application/json\"}.");
                break;

            case "body":
                ValidationMessage = string.Empty;
                break;

            case "timeout_ms":
                ValidationMessage = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
                    && timeout >= 1
                    && timeout <= 300000
                        ? string.Empty
                        : Localized(
                            "1 이상 300,000 이하의 정수를 입력하세요.",
                            "Enter an integer from 1 through 300,000.");
                break;

            case "condition":
                ValidationMessage = bool.TryParse(raw, out _)
                    ? string.Empty
                    : Localized("true/false 또는 '=' Expression을 입력하세요.", "Enter true/false or an '=' expression.");
                break;

            default:
                ValidationMessage = string.IsNullOrWhiteSpace(raw)
                    ? Localized("값을 입력하세요.", "Enter a value.")
                    : string.Empty;
                break;
        }

        return !HasError;
    }

    public void SetEditingEnabled(bool enabled)
    {
        IsEditingEnabled = enabled;
    }

    private string Localized(string korean, string english) =>
        string.Equals(_localization.EffectiveLanguage, "en-US", StringComparison.OrdinalIgnoreCase)
            ? english
            : korean;

    private static bool TryDouble(string raw, out double value) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !double.IsNaN(value)
        && !double.IsInfinity(value);

    private static bool IsAbsoluteHttpUrl(string raw)
    {
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
               && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJsonObjectOrEmpty(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            JObject.Parse(raw);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
