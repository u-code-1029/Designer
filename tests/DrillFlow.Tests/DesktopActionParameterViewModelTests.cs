using System;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopActionParameterViewModelTests
{
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
