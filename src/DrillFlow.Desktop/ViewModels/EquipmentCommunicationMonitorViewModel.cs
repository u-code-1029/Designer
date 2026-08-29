using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrillFlow.Application.Communication;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.ViewModels;

public enum EquipmentScreenConnectionState
{
    Paused,
    Connecting,
    Connected,
    Faulted
}

public sealed class EquipmentCommunicationMonitorViewModel : ObservableObject, IEquipmentExchangeTraceSink
{
    private const int MaximumTerminalEntries = 500;

    private readonly ILocalizationService _localization;
    private readonly IEquipmentScreenPopOutService _popOutService;
    private readonly IEquipmentExchangePathLauncher _pathLauncher;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, EquipmentCommunicationEntryViewModel> _pending =
        new(StringComparer.Ordinal);
    private bool _isPreviewPlaying;
    private bool _isPopOutOpen;
    private bool _isSignalRConnected;
    private bool _isSignalRFaulted;
    private ImageSource? _latestFrameSource;
    private string _lastPathLaunchError = string.Empty;
    private bool _isAutoScrollEnabled = true;

    public EquipmentCommunicationMonitorViewModel(
        ILocalizationService localization,
        IEquipmentScreenPopOutService popOutService,
        IEquipmentExchangePathLauncher pathLauncher)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _popOutService = popOutService ?? throw new ArgumentNullException(nameof(popOutService));
        _pathLauncher = pathLauncher ?? throw new ArgumentNullException(nameof(pathLauncher));
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Entries = new ObservableCollection<EquipmentCommunicationEntryViewModel>();
        ClearTerminalCommand = new RelayCommand(ClearTerminal, () => Entries.Count > 0);
        OpenPathCommand = new RelayCommand<string>(OpenPath, path => !string.IsNullOrWhiteSpace(path));
        TogglePreviewCommand = new RelayCommand(TogglePreview);
        OpenPopOutCommand = new RelayCommand(() => _popOutService.Show(this));

        Entries.CollectionChanged += (_, _) =>
        {
            ClearTerminalCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasEntries));
        };
        _localization.LanguageChanged += (_, _) => NotifyLocalizedPreviewProperties();
    }

    public ObservableCollection<EquipmentCommunicationEntryViewModel> Entries { get; }

    public IRelayCommand ClearTerminalCommand { get; }

    public IRelayCommand<string> OpenPathCommand { get; }

    public IRelayCommand TogglePreviewCommand { get; }

    public IRelayCommand OpenPopOutCommand { get; }

    public bool HasEntries => Entries.Count > 0;

    public bool IsAutoScrollEnabled
    {
        get => _isAutoScrollEnabled;
        set => SetProperty(ref _isAutoScrollEnabled, value);
    }

    public int PendingExchangeCount => _pending.Count;

    public string PendingExchangeText => string.Format(
        _localization["CommunicationPendingCount"],
        PendingExchangeCount);

    public bool IsPreviewPlaying
    {
        get => _isPreviewPlaying;
        private set
        {
            if (!SetProperty(ref _isPreviewPlaying, value))
            {
                return;
            }

            if (!value)
            {
                _isSignalRConnected = false;
                _isSignalRFaulted = false;
            }

            NotifyPreviewStateProperties();
        }
    }

    public bool IsPopOutOpen
    {
        get => _isPopOutOpen;
        private set
        {
            if (SetProperty(ref _isPopOutOpen, value))
            {
                NotifyPreviewStateProperties();
            }
        }
    }

    public bool IsEmbeddedPreviewActive => IsPreviewPlaying && !IsPopOutOpen;

    public bool IsPopOutPreviewActive => IsPreviewPlaying && IsPopOutOpen;

    public EquipmentScreenConnectionState ConnectionState =>
        !IsPreviewPlaying
            ? EquipmentScreenConnectionState.Paused
            : _isSignalRFaulted
                ? EquipmentScreenConnectionState.Faulted
                : _isSignalRConnected
                    ? EquipmentScreenConnectionState.Connected
                    : EquipmentScreenConnectionState.Connecting;

    public string ConnectionStateText => ConnectionState switch
    {
        EquipmentScreenConnectionState.Paused => _localization["EquipmentScreenPaused"],
        EquipmentScreenConnectionState.Connecting => _localization["EquipmentScreenConnecting"],
        EquipmentScreenConnectionState.Connected => _localization["EquipmentScreenConnected"],
        EquipmentScreenConnectionState.Faulted => _localization["EquipmentScreenFaulted"],
        _ => string.Empty
    };

    public string PreviewMessage => IsPopOutOpen
        ? _localization["EquipmentScreenShowingInWindow"]
        : IsPreviewPlaying
            ? _localization["EquipmentScreenWaitingForSignalR"]
            : _localization["EquipmentScreenPlayHint"];

    public string PopOutPreviewMessage => IsPreviewPlaying
        ? _localization["EquipmentScreenWaitingForSignalR"]
        : _localization["EquipmentScreenPlayHint"];

    public string PreviewToggleToolTip => IsPreviewPlaying
        ? _localization["EquipmentScreenPause"]
        : _localization["EquipmentScreenPlay"];

    public SymbolRegular PreviewToggleIcon => IsPreviewPlaying
        ? SymbolRegular.Pause24
        : SymbolRegular.Play24;

    public ImageSource? LatestFrameSource
    {
        get => _latestFrameSource;
        private set
        {
            if (SetProperty(ref _latestFrameSource, value))
            {
                OnPropertyChanged(nameof(HasLatestFrame));
            }
        }
    }

    public bool HasLatestFrame => LatestFrameSource is not null;

    public string LastPathLaunchError
    {
        get => _lastPathLaunchError;
        private set => SetProperty(ref _lastPathLaunchError, value);
    }

    public void OnRequestPublished(
        string filePath,
        EquipmentRequestMessage request,
        int attempt)
    {
        if (request is null)
        {
            return;
        }

        Dispatch(() =>
        {
            var key = CreateKey(request.Action, request.CorrelationId);
            if (_pending.TryGetValue(key, out var previous))
            {
                previous.MarkRetried();
            }

            var entry = new EquipmentCommunicationEntryViewModel(
                DateTimeOffset.Now,
                EquipmentCommunicationDirection.Request,
                filePath,
                request.Action,
                request.CorrelationId,
                attempt,
                FormatRequest(request),
                EquipmentCommunicationEntryState.Waiting);
            _pending[key] = entry;
            AddEntry(entry);
            NotifyPendingChanged();
        });
    }

    public void OnResponseMatched(
        string filePath,
        EquipmentResponseMessage response)
    {
        if (response is null)
        {
            return;
        }

        Dispatch(() =>
        {
            var key = CreateKey(response.Action, response.CorrelationId);
            if (_pending.TryGetValue(key, out var requestEntry))
            {
                requestEntry.MarkMatched();
                _pending.Remove(key);
            }

            AddEntry(new EquipmentCommunicationEntryViewModel(
                DateTimeOffset.Now,
                EquipmentCommunicationDirection.Response,
                filePath,
                response.Action,
                response.CorrelationId,
                0,
                FormatResponse(response),
                EquipmentCommunicationEntryState.Matched));
            NotifyPendingChanged();
        });
    }

    public void OnExchangeStopped(
        string filePath,
        EquipmentRequestMessage request,
        string reason)
    {
        if (request is null)
        {
            return;
        }

        Dispatch(() =>
        {
            var key = CreateKey(request.Action, request.CorrelationId);
            if (_pending.TryGetValue(key, out var requestEntry))
            {
                requestEntry.MarkFailed(reason);
                _pending.Remove(key);
            }

            NotifyPendingChanged();
        });
    }

    /// <summary>
    /// SignalR integration can publish its latest decoded frame through this single-slot surface.
    /// Older frames are intentionally replaced rather than queued.
    /// </summary>
    public void PublishLatestFrame(ImageSource? frame, bool isConnected)
    {
        Dispatch(() =>
        {
            LatestFrameSource = frame;
            _isSignalRConnected = isConnected;
            _isSignalRFaulted = false;
            NotifyPreviewStateProperties();
        });
    }

    public void SetSignalRFaulted()
    {
        Dispatch(() =>
        {
            _isSignalRFaulted = true;
            _isSignalRConnected = false;
            NotifyPreviewStateProperties();
        });
    }

    public void EnterPopOutMode() => IsPopOutOpen = true;

    public void ExitPopOutMode() => IsPopOutOpen = false;

    private void TogglePreview() => IsPreviewPlaying = !IsPreviewPlaying;

    private void ClearTerminal()
    {
        Entries.Clear();
    }

    private void OpenPath(string? filePath)
    {
        try
        {
            _pathLauncher.OpenFileLocation(filePath ?? string.Empty);
            LastPathLaunchError = string.Empty;
        }
        catch (Exception exception)
        {
            LastPathLaunchError = exception.Message;
        }
    }

    private void AddEntry(EquipmentCommunicationEntryViewModel entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaximumTerminalEntries)
        {
            Entries.RemoveAt(0);
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private void NotifyPendingChanged()
    {
        OnPropertyChanged(nameof(PendingExchangeCount));
        OnPropertyChanged(nameof(PendingExchangeText));
    }

    private void NotifyPreviewStateProperties()
    {
        OnPropertyChanged(nameof(IsEmbeddedPreviewActive));
        OnPropertyChanged(nameof(IsPopOutPreviewActive));
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(PreviewMessage));
        OnPropertyChanged(nameof(PopOutPreviewMessage));
        OnPropertyChanged(nameof(PreviewToggleToolTip));
        OnPropertyChanged(nameof(PreviewToggleIcon));
    }

    private void NotifyLocalizedPreviewProperties()
    {
        OnPropertyChanged(nameof(PendingExchangeText));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(PreviewMessage));
        OnPropertyChanged(nameof(PopOutPreviewMessage));
        OnPropertyChanged(nameof(PreviewToggleToolTip));
    }

    private static string CreateKey(string action, int correlationId) =>
        action + ":" + correlationId;

    private static string FormatRequest(EquipmentRequestMessage request)
    {
        var document = new JObject
        {
            ["type"] = request.Type,
            ["correlation_id"] = request.CorrelationId,
            ["action"] = request.Action
        };
        AddProperties(document, request.Parameters);
        return document.ToString(Formatting.Indented);
    }

    private static string FormatResponse(EquipmentResponseMessage response)
    {
        var document = new JObject
        {
            ["type"] = response.Type,
            ["correlation_id"] = response.CorrelationId,
            ["action"] = response.Action,
            ["result"] = response.Result
        };
        AddProperties(document, response.Properties);
        return document.ToString(Formatting.Indented);
    }

    private static void AddProperties(
        JObject document,
        IReadOnlyDictionary<string, object?> properties)
    {
        foreach (var property in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            try
            {
                document[property.Key] = property.Value is null
                    ? JValue.CreateNull()
                    : JToken.FromObject(property.Value);
            }
            catch (JsonException)
            {
                document[property.Key] = property.Value?.ToString();
            }
        }
    }
}
