using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;

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
        _localization.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label));
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

            case "move_x":
            case "move_y":
                ValidationMessage = TryDouble(raw, out var move) && Math.Abs(move) < 0.5
                    ? string.Empty
                    : Localized("-0.5m보다 크고 0.5m보다 작은 값을 입력하세요.", "Enter a value strictly between -0.5m and 0.5m.");
                break;

            case "thickness":
                ValidationMessage = TryDouble(raw, out var thickness) && thickness > 0 && thickness <= 0.0024
                    ? string.Empty
                    : Localized("0m보다 크고 2.4mm 이하인 값을 입력하세요.", "Enter a value greater than 0m and no more than 2.4mm.");
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

            case "drill_result_path":
                ValidationMessage = string.IsNullOrWhiteSpace(raw)
                    ? Localized("장비가 결과를 저장할 경로를 입력하세요.", "Enter the destination path used by the equipment.")
                    : string.Empty;
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
}
