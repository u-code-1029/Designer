using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DrillFlow.Desktop.Behaviors;
using DrillFlow.Desktop.Bootstrap;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

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
        ImmediateToolTipPolicy.Initialize();

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

            var paths = DesktopApplicationPaths.CreateDefault();
            DesktopLogging.ConfigureBootstrapLogger(paths);
            _host = DesktopHostFactory.Create(paths);

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

}
