using System;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopActionParameterViewModelTests
{
    [Theory]
    [InlineData("move_mode", "relative,absolute")]
    [InlineData("lens_mode", "lens1,lens2,no_change")]
    [InlineData("method", "GET,POST")]
    [InlineData("condition", "true,false")]
    public void FiniteStringParameter_ExposesEditableSelectorSuggestions(
        string name,
        string expectedValues)
    {
        var viewModel = new ActionParameterViewModel(
            name,
            "Label",
            ParameterBinding.Literal(string.Empty),
            WorkflowNodeKind.Stage,
            new MutableLocalizationService());

        Assert.True(viewModel.HasSuggestedValues);
        Assert.Equal(expectedValues, string.Join(",", viewModel.SuggestedValues));
    }

    [Fact]
    public void FreeFormParameter_DoesNotExposeSelectorSuggestions()
    {
        var viewModel = new ActionParameterViewModel(
            "stage_x",
            "Label",
            ParameterBinding.Literal("0"),
            WorkflowNodeKind.Stage,
            new MutableLocalizationService());

        Assert.False(viewModel.HasSuggestedValues);
        Assert.Empty(viewModel.SuggestedValues);
    }

    [Fact]
    public void SuggestedParameter_StillAcceptsExpressionText()
    {
        var viewModel = new ActionParameterViewModel(
            "move_mode",
            "Label",
            ParameterBinding.Literal("relative"),
            WorkflowNodeKind.Stage,
            new MutableLocalizationService());

        viewModel.Value = "=previous.parameters.move_mode";

        Assert.True(viewModel.Validate());
        Assert.True(viewModel.IsExpression);
        Assert.Equal("=previous.parameters.move_mode", viewModel.Value);
    }

    [Fact]
    public void LanguageChange_RevalidatesExistingParameterMessageImmediately()
    {
        var localization = new MutableLocalizationService();
        var viewModel = new ActionParameterViewModel(
            "frame_count",
            "ParamFrameCount",
            ParameterBinding.Literal("3"),
            WorkflowNodeKind.Integration,
            localization);

        Assert.False(viewModel.Validate());
        Assert.Contains("power of two", viewModel.ValidationMessage, StringComparison.OrdinalIgnoreCase);

        localization.ApplyLanguage("ko-KR");

        Assert.Contains("2의 거듭제곱", viewModel.ValidationMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lens1")]
    [InlineData("lens2")]
    [InlineData("no_change")]
    public void LensMode_AcceptsEveryContractValue(string value)
    {
        var viewModel = new ActionParameterViewModel(
            "lens_mode",
            "ParamLensMode",
            ParameterBinding.Literal(value),
            WorkflowNodeKind.Lens,
            new MutableLocalizationService());

        Assert.True(viewModel.Validate());
        Assert.False(viewModel.HasError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lens3")]
    [InlineData("none")]
    public void LensMode_RejectsValuesOutsideTheContract(string value)
    {
        var viewModel = new ActionParameterViewModel(
            "lens_mode",
            "ParamLensMode",
            ParameterBinding.Literal(value),
            WorkflowNodeKind.Lens,
            new MutableLocalizationService());

        Assert.False(viewModel.Validate());
        Assert.True(viewModel.HasError);
    }

    [Theory]
    [InlineData("2.04E-6", true)]
    [InlineData("0.00000204", true)]
    [InlineData("0", false)]
    [InlineData("2.4E-3", false)]
    [InlineData("NaN", false)]
    public void AutoContrastBrightnessHfw_UsesTheSharedHfwRange(
        string value,
        bool expectedValid)
    {
        var viewModel = new ActionParameterViewModel(
            "hfw",
            "ParamHfw",
            ParameterBinding.Literal(value),
            WorkflowNodeKind.AutoContrastBrightness,
            new MutableLocalizationService());

        Assert.Equal(expectedValid, viewModel.Validate());
        Assert.Equal(!expectedValid, viewModel.HasError);
    }

    private sealed class MutableLocalizationService : ILocalizationService
    {
        public event EventHandler? LanguageChanged;

        public string SelectedLanguage { get; private set; } = "en-US";

        public string EffectiveLanguage => SelectedLanguage;

        public string this[string key] => key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
            SelectedLanguage = language;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
