using System;
using System.Collections.Generic;
using DrillFlow.Application.Communication;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Wpf.Ui.Controls;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopEquipmentCommunicationMonitorViewModelTests
{
    [Fact]
    public void Constructor_StartsEquipmentPreviewPaused()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsPreviewPlaying);
        Assert.False(viewModel.IsEmbeddedPreviewActive);
        Assert.False(viewModel.IsPopOutPreviewActive);
        Assert.False(viewModel.IsPopOutOpen);
        Assert.Equal(EquipmentScreenConnectionState.Paused, viewModel.ConnectionState);
        Assert.Equal("Paused", viewModel.ConnectionStateText);
        Assert.Equal("Press play", viewModel.PreviewMessage);
        Assert.Equal(SymbolRegular.Play24, viewModel.PreviewToggleIcon);
    }

    [Fact]
    public void RequestAndResponseTrace_TracksPendingExchangeAndMarksPairMatched()
    {
        var viewModel = CreateViewModel();
        var request = new EquipmentRequestMessage(
            401,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = 1E-3,
                ["stage_y"] = -2E-3
            });
        var requestPath = @"C:\Equipment\request.xml";

        viewModel.OnRequestPublished(requestPath, request, 1);

        var requestEntry = Assert.Single(viewModel.Entries);
        Assert.Equal(1, viewModel.PendingExchangeCount);
        Assert.Equal("1 pending", viewModel.PendingExchangeText);
        Assert.Equal(EquipmentCommunicationDirection.Request, requestEntry.Direction);
        Assert.Equal(EquipmentCommunicationEntryState.Waiting, requestEntry.State);
        Assert.Equal(requestPath, requestEntry.FilePath);
        Assert.Equal(EquipmentActionNames.Stage, requestEntry.Action);
        Assert.Equal(401, requestEntry.CorrelationId);
        Assert.Equal(1, requestEntry.Attempt);
        Assert.Contains("\"type\": \"request\"", requestEntry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"stage_x\": 0.001", requestEntry.PayloadJson, StringComparison.Ordinal);

        var response = new EquipmentResponseMessage(
            401,
            EquipmentActionNames.Stage,
            0,
            new Dictionary<string, object?>
            {
                ["current_stage_x"] = 0.25,
                ["current_stage_y"] = -0.5
            });
        var responsePath = @"C:\Equipment\response.xml";

        viewModel.OnResponseMatched(responsePath, response);

        Assert.Equal(0, viewModel.PendingExchangeCount);
        Assert.Equal("0 pending", viewModel.PendingExchangeText);
        Assert.Equal(EquipmentCommunicationEntryState.Matched, requestEntry.State);
        Assert.Equal(2, viewModel.Entries.Count);
        var responseEntry = viewModel.Entries[1];
        Assert.Equal(EquipmentCommunicationDirection.Response, responseEntry.Direction);
        Assert.Equal(EquipmentCommunicationEntryState.Matched, responseEntry.State);
        Assert.Equal(responsePath, responseEntry.FilePath);
        Assert.Equal(EquipmentActionNames.Stage, responseEntry.Action);
        Assert.Equal(401, responseEntry.CorrelationId);
        Assert.Contains("\"type\": \"response\"", responseEntry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"result\": 0", responseEntry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"current_stage_x\": 0.25", responseEntry.PayloadJson, StringComparison.Ordinal);
    }

    private static EquipmentCommunicationMonitorViewModel CreateViewModel()
    {
        return new EquipmentCommunicationMonitorViewModel(
            new StubLocalizationService(),
            new StubPopOutService(),
            new StubPathLauncher());
    }

    private sealed class StubLocalizationService : ILocalizationService
    {
        private static readonly IReadOnlyDictionary<string, string> Values =
            new Dictionary<string, string>
            {
                ["CommunicationPendingCount"] = "{0} pending",
                ["EquipmentScreenPaused"] = "Paused",
                ["EquipmentScreenConnecting"] = "Connecting",
                ["EquipmentScreenConnected"] = "Connected",
                ["EquipmentScreenFaulted"] = "Faulted",
                ["EquipmentScreenShowingInWindow"] = "Showing in window",
                ["EquipmentScreenWaitingForSignalR"] = "Waiting for SignalR",
                ["EquipmentScreenPlayHint"] = "Press play",
                ["EquipmentScreenPause"] = "Pause",
                ["EquipmentScreenPlay"] = "Play"
            };

        public event EventHandler? LanguageChanged;

        public string SelectedLanguage => "en-US";

        public string EffectiveLanguage => "en-US";

        public string this[string key] => Values.TryGetValue(key, out var value) ? value : key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class StubPopOutService : IEquipmentScreenPopOutService
    {
        public void Show(EquipmentCommunicationMonitorViewModel monitor)
        {
        }
    }

    private sealed class StubPathLauncher : IEquipmentExchangePathLauncher
    {
        public string OpenFileLocation(string filePath) => filePath;
    }
}
