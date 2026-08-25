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

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\DrillFlow.Designer.SingleInstance",
            createdNew: out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            MessageBox.Show(
                "DrillFlow Designer is already running.\nDrillFlow Designer가 이미 실행 중입니다.",
                "DrillFlow Designer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        try
        {
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
                    services.AddSingleton<IWorkflowDocumentService, WorkflowDocumentService>();
                    services.AddSingleton<IWorkflowExecutionFacade, WorkflowExecutionFacade>();
                    services.AddSingleton<IFileDialogService, FileDialogService>();
                    services.AddSingleton<IResponseSimulationDialogService, ResponseSimulationDialogService>();

                    services.AddSingleton<MainWindowViewModel>();
                    services.AddSingleton<MainPageViewModel>();
                    services.AddSingleton<SettingsPageViewModel>();

                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<MainPage>();
                    services.AddSingleton<SettingsPage>();
                    services.AddNavigationViewPageProvider();
                })
                .Build();

            await _host.StartAsync().ConfigureAwait(true);

            var localization = _host.Services.GetRequiredService<ILocalizationService>();
            localization.Initialize();

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            Log.Information("DrillFlow Designer started");
            base.OnStartup(e);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed");
            MessageBox.Show(
                exception.Message,
                "DrillFlow Designer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Host shutdown failed");
        }
        finally
        {
            Log.Information("DrillFlow Designer stopped");
            Log.CloseAndFlush();

            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
                _ownsSingleInstanceMutex = false;
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            e.Exception.Message,
            "DrillFlow Designer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
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
