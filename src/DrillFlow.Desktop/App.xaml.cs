using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DrillFlow.Application;
using DrillFlow.Application.Communication;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using DrillFlow.Desktop.Views;
using DrillFlow.Infrastructure;
using DrillFlow.Infrastructure.Communication;
using DrillFlow.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Serilog;
using Wpf.Ui.DependencyInjection;

namespace DrillFlow.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _handlingUnhandledException;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: @"Local\DrillFlow.Designer.SingleInstance",
                createdNew: out _ownsSingleInstanceMutex);
            if (!_ownsSingleInstanceMutex)
            {
                await ShowBootstrapMessageAsync(
                    "DrillFlow Designer",
                    "DrillFlow Designer is already running.\nDrillFlow Designer가 이미 실행 중입니다.");
                Shutdown();
                return;
            }

            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrillFlow",
                "Logs");
            Directory.CreateDirectory(logDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .WriteTo.File(
                    Path.Combine(logDirectory, "bootstrap-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                // Serilog.Extensions.Hosting does not expose ReloadableLogger on its net462 asset;
                // this early logger still serves as the bootstrap logger and is replaced by UseSerilog.
                .CreateLogger();

            var userSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrillFlow",
                "settings.json");

            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(configuration =>
                {
                    configuration.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                    configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                })
                .UseSerilog((_, _, logger) => logger
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.Debug()
                    .WriteTo.File(
                        Path.Combine(logDirectory, "drillflow-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        shared: true))
                .ConfigureServices((context, services) =>
                {
                    services.Configure<DesignerOptions>(context.Configuration.GetSection("DrillFlow"));
                    var designerOptions = LoadStartupOptions(
                        userSettingsPath,
                        context.Configuration.GetSection("DrillFlow").Get<DesignerOptions>()
                        ?? new DesignerOptions());

                    services.AddDrillFlowApplication();
                    services.AddDrillFlowInfrastructure(context.Configuration);
                    services.AddSingleton<WorkflowValidator>();
                    services.PostConfigure<EquipmentCommunicationOptions>(options =>
                    {
                        var communication = designerOptions.Communication ?? new CommunicationSettings();
                        ApplyCommunicationSettings(options, communication);
                    });

                    services.AddSingleton<IUserSettingsStore, UserSettingsStore>();
                    services.AddSingleton<ILocalizationService, LocalizationService>();
                    services.AddSingleton<IApplicationThemeService, ApplicationThemeService>();
                    services.AddSingleton<IWorkflowDocumentService, WorkflowDocumentService>();
                    services.AddSingleton<IWorkflowExecutionFacade, WorkflowExecutionFacade>();
                    services.AddSingleton<IFileDialogService, FileDialogService>();
                    services.AddSingleton<IContentDialogGate, ContentDialogGate>();
                    services.AddSingleton<IUserDialogService, UserDialogService>();
                    services.AddSingleton<ITemporaryResponseImageService, TemporaryResponseImageService>();
                    services.AddSingleton<ILiveCaptureSnapshotStore, LiveCaptureSnapshotStore>();
                    services.AddSingleton<ILiveImageDecoder, LiveImageDecoder>();
                    services.AddSingleton<IDefaultFileLauncher, DefaultFileLauncher>();
                    services.AddSingleton<IResponseSimulationDialogService, ResponseSimulationDialogService>();
                    services.AddSingleton<IExchangeFolderLauncher, ExchangeFolderLauncher>();

                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<MainPageViewModel>();
                    services.AddSingleton<LiveInteractionPageViewModel>();
                    services.AddSingleton<SettingsPageViewModel>();

                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<MainPage>();
                    services.AddSingleton<LiveInteractionPage>();
                    services.AddSingleton<SettingsPage>();
                    services.AddNavigationViewPageProvider();
                })
                .Build();

            await _host.StartAsync().ConfigureAwait(true);

            // Resolve this host-owned singleton at startup so it can remove images left behind by
            // an abnormal termination even when the response simulator is not opened this run.
            _host.Services.GetRequiredService<ITemporaryResponseImageService>();
            _host.Services.GetRequiredService<ILiveCaptureSnapshotStore>();

            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            localization.Initialize();
            var theme = _host.Services.GetRequiredService<IApplicationThemeService>();
            theme.Initialize();

            DispatcherUnhandledException += OnDispatcherUnhandledException;

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            Log.Information("DrillFlow Designer started");
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed");
            await ShowStartupFailureAsync(exception);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            try
            {
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Host stop failed");
            }
            finally
            {
                try
                {
                    // Disposable singletons (including temporary response images) must be
                    // released even when hosted-service shutdown times out or throws.
                    host.Dispose();
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Host disposal failed");
                }
            }
        }

        Log.Information("DrillFlow Designer stopped");
        Log.CloseAndFlush();

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log.Error(e.Exception, "Unhandled UI exception");
        if (_handlingUnhandledException)
        {
            return;
        }

        _handlingUnhandledException = true;
        try
        {
            var dialogs = _host?.Services.GetService<IUserDialogService>();
            var localization = _host?.Services.GetService<ILocalizationService>();
            if (dialogs is not null)
            {
                await dialogs.ShowMessageAsync(
                    localization?["ApplicationErrorTitle"] ?? "DrillFlow Designer",
                    e.Exception.Message);
            }
            else
            {
                await ShowBootstrapMessageAsync("DrillFlow Designer", e.Exception.Message);
            }
        }
        catch (Exception dialogException)
        {
            Log.Error(dialogException, "Could not display the unhandled-error ContentDialog");
        }
        finally
        {
            _handlingUnhandledException = false;
        }
    }

    private async Task ShowStartupFailureAsync(Exception exception)
    {
        try
        {
            var dialogs = _host?.Services.GetService<IUserDialogService>();
            if (dialogs is not null && MainWindow?.IsLoaded == true)
            {
                await dialogs.ShowMessageAsync("DrillFlow Designer", exception.Message);
                return;
            }

            await ShowBootstrapMessageAsync("DrillFlow Designer", exception.Message);
        }
        catch (Exception dialogException)
        {
            Log.Error(dialogException, "Could not display the startup-failure ContentDialog");
        }
    }

    private static async Task ShowBootstrapMessageAsync(string title, string message)
    {
        var window = new StartupDialogWindow();
        await window.ShowMessageAsync(title, message);
    }

    private static DesignerOptions LoadStartupOptions(string userSettingsPath, DesignerOptions fallback)
    {
        try
        {
            if (!File.Exists(userSettingsPath))
            {
                return fallback;
            }

            var root = JObject.Parse(File.ReadAllText(userSettingsPath));
            var persisted = (root["DrillFlow"] ?? root).ToObject<UserPreferences>();
            if (persisted?.Communication is null)
            {
                return fallback;
            }

            var candidate = new DesignerOptions
            {
                Language = string.IsNullOrWhiteSpace(persisted.Language)
                    ? fallback.Language
                    : persisted.Language,
                Theme = ThemeSelection.Normalize(persisted.Theme),
                Communication = persisted.Communication
            };

            var communicationOptions = new EquipmentCommunicationOptions();
            ApplyCommunicationSettings(communicationOptions, candidate.Communication);
            var validation = new EquipmentCommunicationOptionsValidator().Validate(null, communicationOptions);
            if (validation.Failed)
            {
                Log.Warning(
                    "Persisted communication settings are invalid and will not be applied at startup: {Failures}",
                    string.Join("; ", validation.Failures));
                return fallback;
            }

            return candidate;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not apply persisted settings during startup");
            return fallback;
        }
    }

    private static void ApplyCommunicationSettings(
        EquipmentCommunicationOptions options,
        CommunicationSettings communication)
    {
        options.ExchangeDirectory = communication.ExchangeFolder;
        options.RequestFileName = communication.RequestFileName;
        options.ResponseFileName = communication.ResponseFileName;
        options.EquipmentRequestLifecycle = Enum.TryParse<EquipmentRequestFileLifecycle>(
            communication.EquipmentRequestHandling,
            true,
            out var requestLifecycle)
            ? requestLifecycle
            : (EquipmentRequestFileLifecycle)(-1);
        options.ApplicationRequestLifecycle = Enum.TryParse<ApplicationRequestFileLifecycle>(
            communication.AppRequestHandling,
            true,
            out var applicationRequestLifecycle)
            ? applicationRequestLifecycle
            : (ApplicationRequestFileLifecycle)(-1);
        options.ApplicationResponseLifecycle = Enum.TryParse<ApplicationResponseFileLifecycle>(
            communication.AppResponseHandling,
            true,
            out var responseLifecycle)
            ? responseLifecycle
            : (ApplicationResponseFileLifecycle)(-1);
        options.ResponseTimeout = TimeSpan.FromMilliseconds(communication.ResponseTimeoutMilliseconds);
        options.RetryEnabled = communication.RetryEnabled;
        options.MaximumRetryCount = communication.MaximumRetryCount;
        options.RetryDelay = TimeSpan.FromMilliseconds(communication.RetryIntervalMilliseconds);
        options.PollingInterval = TimeSpan.FromMilliseconds(communication.PollingIntervalMilliseconds);
    }
}
