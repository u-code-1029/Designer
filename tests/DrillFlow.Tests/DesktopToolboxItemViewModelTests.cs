using System;
using System.Collections.Generic;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopToolboxItemViewModelTests
{
    [Fact]
    public void MatchesSearch_UsesTitleDescriptionCategoryKindAndContractTokenWithMultipleTerms()
    {
        var localization = new StubLocalizationService(new Dictionary<string, string>
        {
            ["ActionStage"] = "Stage move",
            ["ToolboxStageDescription"] = "Move the sample relatively or absolutely",
            ["EquipmentActions"] = "Equipment actions"
        });
        var item = new ToolboxItemViewModel(
            WorkflowNodeKind.Stage,
            "ActionStage",
            "ToolboxStageDescription",
            SymbolRegular.ArrowMove20,
            ToolboxItemCategory.Equipment,
            localization);

        Assert.True(item.MatchesSearch("stage"));
        Assert.True(item.MatchesSearch("sample absolute"));
        Assert.True(item.MatchesSearch("equipment Stage"));
        Assert.Equal("stage", item.ActionToken);
        Assert.Contains("stage", item.MetadataLabel, StringComparison.Ordinal);
        Assert.False(item.MatchesSearch("camera"));
        Assert.True(item.MatchesSearch("   "));
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Stage, "stage")]
    [InlineData(WorkflowNodeKind.Camera, "camera")]
    [InlineData(WorkflowNodeKind.Focus, "focus")]
    [InlineData(WorkflowNodeKind.Integration, "integration")]
    [InlineData(WorkflowNodeKind.Live, "live")]
    [InlineData(WorkflowNodeKind.Om, "om")]
    [InlineData(WorkflowNodeKind.Lens, "lens")]
    [InlineData(WorkflowNodeKind.AutoContrastBrightness, "acb")]
    [InlineData(WorkflowNodeKind.Abort, "abort")]
    [InlineData(WorkflowNodeKind.Http, "http")]
    [InlineData(WorkflowNodeKind.Delay, "delay")]
    [InlineData(WorkflowNodeKind.Repeat, "repeat")]
    [InlineData(WorkflowNodeKind.Conditional, "if")]
    public void MatchesSearch_RecognizesCompactActionToken(WorkflowNodeKind kind, string token)
    {
        var localization = new StubLocalizationService(new Dictionary<string, string>());
        var item = new ToolboxItemViewModel(
            kind,
            "title",
            "description",
            SymbolRegular.Circle20,
            ToolboxItemCategory.Equipment,
            localization);

        Assert.Equal(token, item.ActionToken);
        Assert.True(item.MatchesSearch(token));
    }

    [Fact]
    public void LanguageChange_RefreshesLocalizedCategoryUsedBySearch()
    {
        var localization = new StubLocalizationService(new Dictionary<string, string>
        {
            ["ActionDelay"] = "Delay",
            ["ToolboxDelayDescription"] = "Wait locally",
            ["FlowActions"] = "Designer actions"
        });
        var item = new ToolboxItemViewModel(
            WorkflowNodeKind.Delay,
            "ActionDelay",
            "ToolboxDelayDescription",
            SymbolRegular.Timer20,
            ToolboxItemCategory.Designer,
            localization);

        Assert.True(item.MatchesSearch("Designer"));

        localization.SetValues(new Dictionary<string, string>
        {
            ["ActionDelay"] = "지연",
            ["ToolboxDelayDescription"] = "로컬에서 대기",
            ["FlowActions"] = "디자이너 동작"
        });

        Assert.Equal("디자이너 동작", item.CategoryTitle);
        Assert.True(item.MatchesSearch("디자이너"));
        Assert.False(item.MatchesSearch("Designer"));
    }

    private sealed class StubLocalizationService : ILocalizationService
    {
        private IReadOnlyDictionary<string, string> _values;

        public StubLocalizationService(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public event EventHandler? LanguageChanged;

        public string SelectedLanguage => "Auto";

        public string EffectiveLanguage => "en-US";

        public string this[string key] => _values.TryGetValue(key, out var value) ? value : key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetValues(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
