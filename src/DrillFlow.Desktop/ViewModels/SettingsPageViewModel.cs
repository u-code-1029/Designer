using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrillFlow.Application.Communication;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrillFlow.Desktop.ViewModels;

public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly IUserSettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly EquipmentCommunicationOptions _liveOptions;
    private readonly IWorkflowExecutionFacade _execution;
    private readonly IFileDialogService _fileDialogs;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private string _language = "Auto";
    private string _exchangeFolder = string.Empty;
    private string _requestFileName = "request.json";
    private string _responseFileName = "response.json";
    private string _equipmentRequestHandling = "RetainUntilOverwritten";
    private string _appResponseHandling = "DeleteAfterRead";
    private string _responseTimeoutMilliseconds = "30000";
    private bool _retryEnabled;
    private string _maximumRetryCount = "1";
    private string _retryIntervalMilliseconds = "1000";
    private string _pollingIntervalMilliseconds = "250";
    private bool _isExecutionBusy;
    private string _validationMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private bool _isTesting;

    public SettingsPageViewModel(
        IUserSettingsStore settingsStore,
        ILocalizationService localization,
        IOptions<EquipmentCommunicationOptions> liveOptions,
        IWorkflowExecutionFacade execution,
        IFileDialogService fileDialogs,
        ILogger<SettingsPageViewModel> logger)
    {
        _settingsStore = settingsStore;
        _localization = localization;
        _liveOptions = liveOptions.Value;
        _execution = execution;
        _fileDialogs = fileDialogs;
        _logger = logger;

        SaveCommand = new RelayCommand(Save, () => CanEditSettings);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => CanEditSettings);
        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => CanEditSettings);

        Load();
        // Invalid persisted drafts remain visible so the operator can correct them, but they
        // must never replace the valid options selected during startup.
        if (Validate())
        {
            // The transport retains the IOptions.Value instance, so mutating that same instance
            // applies a valid persisted value immediately even before the user saves again.
            ApplyToLiveOptions(BuildSettings());
        }
        else
        {
            StatusMessage = _localization["SettingsValidationFailed"];
            StatusIsError = true;
        }
        IsExecutionBusy = IsBusyState(_execution.State);
        _execution.RunStateChanged += (_, eventArgs) => Dispatch(() => IsExecutionBusy = IsBusyState(eventArgs.State));
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(StatusMessage));
        };
    }

    public IRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand TestConnectionCommand { get; }

    public IRelayCommand BrowseFolderCommand { get; }

    public string[] LanguageChoices { get; } = { "Auto", "ko-KR", "en-US" };

    public string[] EquipmentRequestHandlingChoices { get; } =
    {
        "EquipmentDeletesAfterRead",
        "RetainUntilOverwritten"
    };

    public string[] AppResponseHandlingChoices { get; } =
    {
        "DeleteAfterRead",
        "RetainUntilOverwritten"
    };

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value ?? "Auto");
    }

    public string ExchangeFolder
    {
        get => _exchangeFolder;
        set => SetProperty(ref _exchangeFolder, value ?? string.Empty);
    }

    public string RequestFileName
    {
        get => _requestFileName;
        set => SetProperty(ref _requestFileName, value ?? string.Empty);
    }

    public string ResponseFileName
    {
        get => _responseFileName;
        set => SetProperty(ref _responseFileName, value ?? string.Empty);
    }

    public string EquipmentRequestHandling
    {
        get => _equipmentRequestHandling;
        set => SetProperty(ref _equipmentRequestHandling, value ?? string.Empty);
    }

    public string AppResponseHandling
    {
        get => _appResponseHandling;
        set => SetProperty(ref _appResponseHandling, value ?? string.Empty);
    }

    public string ResponseTimeoutMilliseconds
    {
        get => _responseTimeoutMilliseconds;
        set => SetProperty(ref _responseTimeoutMilliseconds, value);
    }

    public bool RetryEnabled
    {
        get => _retryEnabled;
        set => SetProperty(ref _retryEnabled, value);
    }

    public string MaximumRetryCount
    {
        get => _maximumRetryCount;
        set => SetProperty(ref _maximumRetryCount, value);
    }

    public string RetryIntervalMilliseconds
    {
        get => _retryIntervalMilliseconds;
        set => SetProperty(ref _retryIntervalMilliseconds, value);
    }

    public string PollingIntervalMilliseconds
    {
        get => _pollingIntervalMilliseconds;
        set => SetProperty(ref _pollingIntervalMilliseconds, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                TestConnectionCommand.NotifyCanExecuteChanged();
                SaveCommand.NotifyCanExecuteChanged();
                BrowseFolderCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditSettings));
            }
        }
    }

    public bool IsExecutionBusy
    {
        get => _isExecutionBusy;
        private set
        {
            if (SetProperty(ref _isExecutionBusy, value))
            {
                if (value)
                {
                    StatusMessage = _localization["SettingsBusy"];
                    StatusIsError = false;
                }

                SaveCommand.NotifyCanExecuteChanged();
                TestConnectionCommand.NotifyCanExecuteChanged();
                BrowseFolderCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditSettings));
            }
        }
    }

    public bool CanEditSettings => !IsExecutionBusy && !IsTesting;

    private void Load()
    {
        var preferences = _settingsStore.Load();
        var communication = preferences.Communication ?? new CommunicationSettings();

        Language = preferences.Language;
        ExchangeFolder = communication.ExchangeFolder;
        RequestFileName = communication.RequestFileName;
        ResponseFileName = communication.ResponseFileName;
        // Preserve unsupported persisted values as an invalid draft. Startup deliberately
        // keeps the validated fallback live options in that case; silently normalizing here
        // would overwrite those safe options as soon as this view model is constructed.
        EquipmentRequestHandling = communication.EquipmentRequestHandling;
        AppResponseHandling = communication.AppResponseHandling;
        ResponseTimeoutMilliseconds = communication.ResponseTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        RetryEnabled = communication.RetryEnabled;
        MaximumRetryCount = communication.MaximumRetryCount.ToString(CultureInfo.InvariantCulture);
        RetryIntervalMilliseconds = communication.RetryIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
        PollingIntervalMilliseconds = communication.PollingIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
    }

    private void Save()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (!Validate())
        {
            StatusMessage = _localization["SettingsValidationFailed"];
            StatusIsError = true;
            return;
        }

        var communication = BuildSettings();
        var preferences = new UserPreferences
        {
            Language = Language,
            Communication = communication
        };

        try
        {
            _settingsStore.Save(preferences);
            ApplyToLiveOptions(communication);
            _localization.ApplyLanguage(Language, false);
            StatusMessage = _localization["SettingsSaved"];
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not save settings");
            StatusMessage = exception.Message;
            StatusIsError = true;
        }
    }

    private async Task TestConnectionAsync()
    {
        if (!CanEditSettings)
        {
            return;
        }

        if (!Validate())
        {
            StatusMessage = _localization["SettingsValidationFailed"];
            StatusIsError = true;
            return;
        }

        IsTesting = true;
        var testPath = Path.Combine(ExchangeFolder, ".drillflow-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(ExchangeFolder);
                File.WriteAllText(testPath, "DrillFlow");
                _ = File.ReadAllText(testPath);
                File.Delete(testPath);
            });

            StatusMessage = _localization["ConnectionTestPassed"];
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Exchange folder test failed for {Folder}", ExchangeFolder);
            StatusMessage = _localization["ConnectionTestFailed"] + " " + exception.Message;
            StatusIsError = true;
            TryDelete(testPath);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool Validate()
    {
        string? failure = null;
        if (string.IsNullOrWhiteSpace(ExchangeFolder) || !Path.IsPathRooted(ExchangeFolder))
        {
            failure = _localization["FolderRequired"];
        }
        else if (!IsLeafFileName(RequestFileName) || !IsLeafFileName(ResponseFileName))
        {
            failure = _localization["FileNameOnly"];
        }
        else if (string.Equals(RequestFileName, ResponseFileName, StringComparison.OrdinalIgnoreCase))
        {
            failure = _localization["RequestResponseDistinct"];
        }
        else if (string.Equals(
                     RequestFileName,
                     EquipmentCommunicationOptions.ExchangeLockFileName,
                     StringComparison.OrdinalIgnoreCase)
                 || string.Equals(
                     ResponseFileName,
                     EquipmentCommunicationOptions.ExchangeLockFileName,
                     StringComparison.OrdinalIgnoreCase))
        {
            failure = _localization["ReservedExchangeFileName"];
        }
        else if (!EquipmentRequestHandlingChoices.Contains(
                     EquipmentRequestHandling,
                     StringComparer.OrdinalIgnoreCase)
                 || !AppResponseHandlingChoices.Contains(
                     AppResponseHandling,
                     StringComparer.OrdinalIgnoreCase))
        {
            failure = _localization["LifecycleRequired"];
        }
        else if (!int.TryParse(ResponseTimeoutMilliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
                 || !int.TryParse(PollingIntervalMilliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var polling)
                 || !int.TryParse(RetryIntervalMilliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retryInterval)
                 || !int.TryParse(MaximumRetryCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries)
                 || timeout <= 0
                 || polling <= 0
                 || retryInterval < 0
                 || retries < 0
                 || (RetryEnabled && retries == 0))
        {
            failure = _localization["PositiveNumberRequired"];
        }

        ValidationMessage = failure ?? string.Empty;
        return failure is null;
    }

    private CommunicationSettings BuildSettings() => new()
    {
        ExchangeFolder = ExchangeFolder.Trim(),
        RequestFileName = RequestFileName.Trim(),
        ResponseFileName = ResponseFileName.Trim(),
        EquipmentRequestHandling = EquipmentRequestHandling,
        AppResponseHandling = AppResponseHandling,
        ResponseTimeoutMilliseconds = int.Parse(ResponseTimeoutMilliseconds, CultureInfo.InvariantCulture),
        RetryEnabled = RetryEnabled,
        MaximumRetryCount = int.Parse(MaximumRetryCount, CultureInfo.InvariantCulture),
        RetryIntervalMilliseconds = int.Parse(RetryIntervalMilliseconds, CultureInfo.InvariantCulture),
        PollingIntervalMilliseconds = int.Parse(PollingIntervalMilliseconds, CultureInfo.InvariantCulture)
    };

    private void BrowseFolder()
    {
        var selected = _fileDialogs.ShowSelectFolderDialog(ExchangeFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            ExchangeFolder = selected!;
        }
    }

    private void ApplyToLiveOptions(CommunicationSettings settings)
    {
        _liveOptions.ExchangeDirectory = settings.ExchangeFolder;
        _liveOptions.RequestFileName = settings.RequestFileName;
        _liveOptions.ResponseFileName = settings.ResponseFileName;
        _liveOptions.EquipmentRequestLifecycle = Enum.TryParse<EquipmentRequestFileLifecycle>(
            settings.EquipmentRequestHandling,
            out var requestLifecycle)
            ? requestLifecycle
            : EquipmentRequestFileLifecycle.RetainUntilOverwritten;
        _liveOptions.ApplicationResponseLifecycle = Enum.TryParse<ApplicationResponseFileLifecycle>(
            settings.AppResponseHandling,
            out var responseLifecycle)
            ? responseLifecycle
            : ApplicationResponseFileLifecycle.DeleteAfterRead;
        _liveOptions.ResponseTimeout = TimeSpan.FromMilliseconds(settings.ResponseTimeoutMilliseconds);
        _liveOptions.RetryEnabled = settings.RetryEnabled;
        _liveOptions.MaximumRetryCount = settings.MaximumRetryCount;
        _liveOptions.RetryDelay = TimeSpan.FromMilliseconds(settings.RetryIntervalMilliseconds);
        _liveOptions.PollingInterval = TimeSpan.FromMilliseconds(settings.PollingIntervalMilliseconds);
    }

    private static bool IsLeafFileName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
               && !Path.IsPathRooted(value)
               && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && !string.IsNullOrWhiteSpace(Path.GetExtension(value));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The test result already reports the original failure.
        }
    }

    private static bool IsBusyState(DrillFlow.Application.Execution.WorkflowRunState state) =>
        state is DrillFlow.Application.Execution.WorkflowRunState.Validating
            or DrillFlow.Application.Execution.WorkflowRunState.Running
            or DrillFlow.Application.Execution.WorkflowRunState.Paused
            or DrillFlow.Application.Execution.WorkflowRunState.Stopping;

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
