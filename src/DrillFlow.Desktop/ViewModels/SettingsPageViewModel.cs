using System;
using System.IO;
using System.Globalization;
using System.ComponentModel;
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
    private readonly IApplicationThemeService _themeService;
    private readonly IWorkflowValidationPolicy _validationPolicy;
    private readonly EquipmentCommunicationOptions _liveOptions;
    private readonly IWorkflowExecutionFacade _execution;
    private readonly LiveInteractionPageViewModel _liveInteraction;
    private readonly IFileDialogService _fileDialogs;
    private readonly IExchangeFolderLauncher _exchangeFolderLauncher;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private string _language = "Auto";
    private string _theme = ThemeSelection.System;
    private bool _validateWorkflowOnEveryChange = true;
    private string _exchangeFolder = string.Empty;
    private string _liveImageFolder = string.Empty;
    private string _requestFileName = "request.xml";
    private string _responseFileName = "response.xml";
    private string _equipmentRequestHandling = "RetainUntilOverwritten";
    private string _appRequestHandling = "DeleteAfterResponse";
    private string _appResponseHandling = "DeleteAfterRead";
    private string _responseTimeoutSeconds = "30";
    private bool _retryEnabled;
    private string _maximumRetryCount = "1";
    private string _retryIntervalMilliseconds = "1000";
    private string _pollingIntervalSeconds = "0.05";
    private string _requestPublishDelaySeconds = "0.1";
    private bool _isExecutionBusy;
    private string _validationMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _statusIsError;
    private bool _isTesting;

    public SettingsPageViewModel(
        IUserSettingsStore settingsStore,
        ILocalizationService localization,
        IApplicationThemeService themeService,
        IWorkflowValidationPolicy validationPolicy,
        IOptions<EquipmentCommunicationOptions> liveOptions,
        IWorkflowExecutionFacade execution,
        LiveInteractionPageViewModel liveInteraction,
        IFileDialogService fileDialogs,
        IExchangeFolderLauncher exchangeFolderLauncher,
        ILogger<SettingsPageViewModel> logger)
    {
        _settingsStore = settingsStore;
        _localization = localization;
        _themeService = themeService;
        _validationPolicy = validationPolicy;
        _liveOptions = liveOptions.Value;
        _execution = execution;
        _liveInteraction = liveInteraction;
        _fileDialogs = fileDialogs;
        _exchangeFolderLauncher = exchangeFolderLauncher;
        _logger = logger;

        SaveCommand = new RelayCommand(Save, () => CanEditSettings);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => CanEditSettings);
        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => CanEditSettings);
        OpenFolderCommand = new RelayCommand(OpenFolder, CanOpenFolder);
        BrowseLiveImageFolderCommand = new RelayCommand(
            BrowseLiveImageFolder,
            () => CanEditSettings);
        OpenLiveImageFolderCommand = new RelayCommand(
            OpenLiveImageFolder,
            CanOpenLiveImageFolder);

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
        _liveInteraction.PropertyChanged += OnLiveInteractionPropertyChanged;
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(StatusMessage));
        };
    }

    public IRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand TestConnectionCommand { get; }

    public IRelayCommand BrowseFolderCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    public IRelayCommand BrowseLiveImageFolderCommand { get; }

    public IRelayCommand OpenLiveImageFolderCommand { get; }

    public string[] LanguageChoices { get; } = { "Auto", "ko-KR", "en-US" };

    public string[] ThemeChoices { get; } =
    {
        ThemeSelection.System,
        ThemeSelection.Light,
        ThemeSelection.Dark
    };

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

    public string[] AppRequestHandlingChoices { get; } =
    {
        "DeleteAfterResponse",
        "RetainUntilOverwritten"
    };

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value ?? "Auto");
    }

    public string Theme
    {
        get => _theme;
        set
        {
            var normalized = ThemeSelection.Normalize(value);
            if (SetProperty(ref _theme, normalized))
            {
                _themeService.ApplyTheme(normalized);
            }
        }
    }

    public bool ValidateWorkflowOnEveryChange
    {
        get => _validateWorkflowOnEveryChange;
        set
        {
            if (SetProperty(ref _validateWorkflowOnEveryChange, value))
            {
                _validationPolicy.Apply(value);
            }
        }
    }

    public string ExchangeFolder
    {
        get => _exchangeFolder;
        set
        {
            if (SetProperty(ref _exchangeFolder, value ?? string.Empty))
            {
                OpenFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string LiveImageFolder
    {
        get => _liveImageFolder;
        set
        {
            if (SetProperty(ref _liveImageFolder, value ?? string.Empty))
            {
                OpenLiveImageFolderCommand.NotifyCanExecuteChanged();
            }
        }
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

    public string AppRequestHandling
    {
        get => _appRequestHandling;
        set => SetProperty(ref _appRequestHandling, value ?? string.Empty);
    }

    public string ResponseTimeoutSeconds
    {
        get => _responseTimeoutSeconds;
        set => SetProperty(ref _responseTimeoutSeconds, value ?? string.Empty);
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

    public string PollingIntervalSeconds
    {
        get => _pollingIntervalSeconds;
        set => SetProperty(ref _pollingIntervalSeconds, value ?? string.Empty);
    }

    public string RequestPublishDelaySeconds
    {
        get => _requestPublishDelaySeconds;
        set => SetProperty(ref _requestPublishDelaySeconds, value ?? string.Empty);
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
                OpenFolderCommand.NotifyCanExecuteChanged();
                BrowseLiveImageFolderCommand.NotifyCanExecuteChanged();
                OpenLiveImageFolderCommand.NotifyCanExecuteChanged();
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
                OpenFolderCommand.NotifyCanExecuteChanged();
                BrowseLiveImageFolderCommand.NotifyCanExecuteChanged();
                OpenLiveImageFolderCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditSettings));
            }
        }
    }

    public bool CanEditSettings => !IsExecutionBusy
                                   && !IsTesting
                                   && !_liveInteraction.IsInteractionActive;

    private void Load()
    {
        var preferences = _settingsStore.Load();
        var communication = preferences.Communication ?? new CommunicationSettings();

        Language = preferences.Language;
        Theme = preferences.Theme;
        ValidateWorkflowOnEveryChange = preferences.ValidateWorkflowOnEveryChange;
        ExchangeFolder = communication.ExchangeFolder;
        LiveImageFolder = communication.ResolveLiveImageFolder();
        RequestFileName = communication.RequestFileName;
        ResponseFileName = communication.ResponseFileName;
        // Preserve unsupported persisted values as an invalid draft. Startup deliberately
        // keeps the validated fallback live options in that case; silently normalizing here
        // would overwrite those safe options as soon as this view model is constructed.
        EquipmentRequestHandling = communication.EquipmentRequestHandling;
        AppRequestHandling = communication.AppRequestHandling;
        AppResponseHandling = communication.AppResponseHandling;
        ResponseTimeoutSeconds = FormatMillisecondsAsSeconds(communication.ResponseTimeoutMilliseconds);
        RetryEnabled = communication.RetryEnabled;
        MaximumRetryCount = communication.MaximumRetryCount.ToString(CultureInfo.InvariantCulture);
        RetryIntervalMilliseconds = communication.RetryIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
        PollingIntervalSeconds = FormatMillisecondsAsSeconds(communication.PollingIntervalMilliseconds);
        RequestPublishDelaySeconds = FormatMillisecondsAsSeconds(
            communication.RequestPublishDelayMilliseconds);
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
            Theme = Theme,
            ValidateWorkflowOnEveryChange = ValidateWorkflowOnEveryChange,
            Communication = communication
        };

        try
        {
            _settingsStore.Save(preferences);
            ApplyToLiveOptions(communication);
            // Display the millisecond-resolution value that was actually persisted/applied when
            // an operator entered more than three fractional second digits.
            ResponseTimeoutSeconds = FormatMillisecondsAsSeconds(
                communication.ResponseTimeoutMilliseconds);
            PollingIntervalSeconds = FormatMillisecondsAsSeconds(
                communication.PollingIntervalMilliseconds);
            RequestPublishDelaySeconds = FormatMillisecondsAsSeconds(
                communication.RequestPublishDelayMilliseconds);
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
        var testName = ".drillflow-write-test-" + Guid.NewGuid().ToString("N") + ".tmp";
        var exchangeTestPath = Path.Combine(ExchangeFolder, testName);
        var liveImageTestPath = Path.Combine(LiveImageFolder, testName);
        try
        {
            await Task.Run(() =>
            {
                TestWritableDirectory(ExchangeFolder, exchangeTestPath);
                if (!string.Equals(
                        ExchangeFolder.TrimEnd('\\', '/'),
                        LiveImageFolder.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    TestWritableDirectory(LiveImageFolder, liveImageTestPath);
                }
            });

            StatusMessage = _localization["ConnectionTestPassed"];
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Communication folder test failed for exchange {ExchangeFolder} and Live images {LiveImageFolder}",
                ExchangeFolder,
                LiveImageFolder);
            StatusMessage = _localization["ConnectionTestFailed"] + " " + exception.Message;
            StatusIsError = true;
            TryDelete(exchangeTestPath);
            TryDelete(liveImageTestPath);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool Validate()
    {
        string? failure = null;
        if (!IsRootedPath(ExchangeFolder))
        {
            failure = _localization["FolderRequired"];
        }
        else if (!IsLeafFileName(RequestFileName) || !IsLeafFileName(ResponseFileName))
        {
            failure = _localization["FileNameOnly"];
        }
        else if (!IsRootedPath(LiveImageFolder))
        {
            failure = _localization["LiveImageFolderRequired"];
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
                 || !AppRequestHandlingChoices.Contains(
                     AppRequestHandling,
                     StringComparer.OrdinalIgnoreCase)
                 || !AppResponseHandlingChoices.Contains(
                     AppResponseHandling,
                     StringComparer.OrdinalIgnoreCase))
        {
            failure = _localization["LifecycleRequired"];
        }
        else if (!TryConvertSecondsToMilliseconds(
                     ResponseTimeoutSeconds,
                     allowZero: false,
                     out _)
                 || !TryConvertSecondsToMilliseconds(
                     PollingIntervalSeconds,
                     allowZero: false,
                     out _)
                 || !TryConvertSecondsToMilliseconds(
                     RequestPublishDelaySeconds,
                     allowZero: true,
                     out _)
                 || !int.TryParse(RetryIntervalMilliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retryInterval)
                 || !int.TryParse(MaximumRetryCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries)
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
        ExchangeFolder = EquipmentCommunicationOptions.NormalizeExchangeDirectory(ExchangeFolder),
        LiveImageFolder = EquipmentCommunicationOptions.NormalizeExchangeDirectory(LiveImageFolder),
        RequestFileName = RequestFileName.Trim(),
        ResponseFileName = ResponseFileName.Trim(),
        EquipmentRequestHandling = EquipmentRequestHandling,
        AppRequestHandling = AppRequestHandling,
        AppResponseHandling = AppResponseHandling,
        ResponseTimeoutMilliseconds = ConvertSecondsToMilliseconds(
            ResponseTimeoutSeconds,
            allowZero: false),
        RetryEnabled = RetryEnabled,
        MaximumRetryCount = int.Parse(MaximumRetryCount, CultureInfo.InvariantCulture),
        RetryIntervalMilliseconds = int.Parse(RetryIntervalMilliseconds, CultureInfo.InvariantCulture),
        PollingIntervalMilliseconds = ConvertSecondsToMilliseconds(
            PollingIntervalSeconds,
            allowZero: false),
        RequestPublishDelayMilliseconds = ConvertSecondsToMilliseconds(
            RequestPublishDelaySeconds,
            allowZero: true)
    };

    private void BrowseFolder()
    {
        var selected = _fileDialogs.ShowSelectFolderDialog(ExchangeFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            ExchangeFolder = selected!;
        }
    }

    private bool CanOpenFolder() => CanEditSettings && IsRootedPath(ExchangeFolder);

    private void OpenFolder()
    {
        try
        {
            var path = _exchangeFolderLauncher.Open(ExchangeFolder);
            StatusMessage = string.Format(_localization["ExchangeFolderOpened"], path);
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not open the draft equipment exchange directory");
            StatusMessage = _localization["ExchangeFolderOpenFailed"] + " " + exception.Message;
            StatusIsError = true;
        }
    }

    private void BrowseLiveImageFolder()
    {
        var selected = _fileDialogs.ShowSelectLiveImageFolderDialog(LiveImageFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            LiveImageFolder = selected!;
        }
    }

    private bool CanOpenLiveImageFolder() =>
        CanEditSettings && IsRootedPath(LiveImageFolder);

    private void OpenLiveImageFolder()
    {
        try
        {
            var path = _exchangeFolderLauncher.Open(LiveImageFolder);
            StatusMessage = string.Format(_localization["LiveImageFolderOpened"], path);
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not open the draft Live image directory");
            StatusMessage = _localization["LiveImageFolderOpenFailed"] + " " + exception.Message;
            StatusIsError = true;
        }
    }

    private void ApplyToLiveOptions(CommunicationSettings settings)
    {
        settings.ApplyTo(_liveOptions);
    }

    internal static string FormatMillisecondsAsSeconds(int milliseconds) =>
        (milliseconds / 1000m).ToString(CultureInfo.InvariantCulture);

    private static int ConvertSecondsToMilliseconds(string value, bool allowZero)
    {
        if (!TryConvertSecondsToMilliseconds(value, allowZero, out var milliseconds))
        {
            throw new InvalidOperationException("The communication timing value is invalid.");
        }

        return milliseconds;
    }

    internal static bool TryConvertSecondsToMilliseconds(
        string value,
        bool allowZero,
        out int milliseconds)
    {
        milliseconds = 0;
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds)
            || double.IsNaN(seconds)
            || double.IsInfinity(seconds)
            || seconds < 0d
            || !allowZero && seconds <= 0d)
        {
            return false;
        }

        var unroundedMilliseconds = seconds * 1000d;
        if (double.IsInfinity(unroundedMilliseconds)
            || unroundedMilliseconds > int.MaxValue)
        {
            return false;
        }

        var roundedMilliseconds = Math.Round(
            unroundedMilliseconds,
            MidpointRounding.AwayFromZero);
        if (roundedMilliseconds > int.MaxValue
            || roundedMilliseconds < 1d && (seconds > 0d || !allowZero))
        {
            return false;
        }

        milliseconds = checked((int)roundedMilliseconds);
        return true;
    }

    private static bool IsLeafFileName(string value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value)
                   && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
                   && !Path.IsPathRooted(value)
                   && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                   && !string.IsNullOrWhiteSpace(Path.GetExtension(value));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsRootedPath(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var path = value.Trim();
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var serverSeparator = path.IndexOfAny(new[] { '\\', '/' }, 2);
                if (serverSeparator <= 2 || serverSeparator == path.Length - 1)
                {
                    return false;
                }

                var shareStart = serverSeparator + 1;
                var shareSeparator = path.IndexOfAny(new[] { '\\', '/' }, shareStart);
                var shareLength = (shareSeparator < 0 ? path.Length : shareSeparator) - shareStart;
                return shareLength > 0;
            }

            return path.Length >= 3
                   && char.IsLetter(path[0])
                   && path[1] == ':'
                   && (path[2] == Path.DirectorySeparatorChar
                       || path[2] == Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
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

    private static void TestWritableDirectory(string directory, string testPath)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(testPath, "DrillFlow");
        _ = File.ReadAllText(testPath);
        File.Delete(testPath);
    }

    private static bool IsBusyState(DrillFlow.Application.Execution.WorkflowRunState state) =>
        state is DrillFlow.Application.Execution.WorkflowRunState.Validating
            or DrillFlow.Application.Execution.WorkflowRunState.Running
            or DrillFlow.Application.Execution.WorkflowRunState.Paused
            or DrillFlow.Application.Execution.WorkflowRunState.Stopping;

    private void OnLiveInteractionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)
            && !string.Equals(
                e.PropertyName,
                nameof(LiveInteractionPageViewModel.IsInteractionActive),
                StringComparison.Ordinal))
        {
            return;
        }

        Dispatch(() =>
        {
            if (_liveInteraction.IsInteractionActive)
            {
                StatusMessage = _localization["SettingsBusy"];
                StatusIsError = false;
            }

            SaveCommand.NotifyCanExecuteChanged();
            TestConnectionCommand.NotifyCanExecuteChanged();
            BrowseFolderCommand.NotifyCanExecuteChanged();
            OpenFolderCommand.NotifyCanExecuteChanged();
            BrowseLiveImageFolderCommand.NotifyCanExecuteChanged();
            OpenLiveImageFolderCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanEditSettings));
        });
    }

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
