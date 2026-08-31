using DrillFlow.Application;
using DrillFlow.Application.Communication;
using DrillFlow.Application.RealtimeVideo;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using DrillFlow.Desktop.Views;
using DrillFlow.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.DependencyInjection;

namespace DrillFlow.Desktop.Bootstrap;

/// <summary>
/// Desktop composition root. Feature registrations live here instead of in WPF lifecycle code.
/// </summary>
internal static class DesktopServiceCollectionExtensions
{
    public static IServiceCollection AddDrillFlowDesktop(
        this IServiceCollection services,
        IConfiguration configuration,
        string userSettingsPath,
        string legacyUserSettingsPath)
    {
        services.Configure<DesignerOptions>(configuration.GetSection("DrillFlow"));
        var designerOptions = StartupSettingsLoader.Load(
            configuration,
            userSettingsPath,
            legacyUserSettingsPath);

        services.AddDrillFlowApplication();
        services.AddOptions<RealtimeVideoOptions>()
            .Bind(configuration.GetSection(RealtimeVideoOptions.SectionName))
            .PostConfigure(options => CopyRealtimeVideo(designerOptions.RealtimeVideo, options))
            .ValidateOnStart();
        services.AddDrillFlowInfrastructure(configuration);
        var communicationOverride = designerOptions.Communication;
        if (communicationOverride is not null)
        {
            services.PostConfigure<EquipmentCommunicationOptions>(options =>
                communicationOverride.ApplyTo(options));
        }

        services.AddDesktopServices();
        services.AddDesktopViewModels();
        services.AddDesktopViews();
        services.AddNavigationViewPageProvider();

        return services;
    }

    private static void AddDesktopServices(this IServiceCollection services)
    {
        services.AddSingleton<IUserSettingsStore, UserSettingsStore>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IApplicationThemeService, ApplicationThemeService>();
        services.AddSingleton<IWorkflowValidationPolicy, WorkflowValidationPolicy>();
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
        services.AddSingleton<IEquipmentExchangePathLauncher, EquipmentExchangePathLauncher>();
        services.AddSingleton<IEquipmentScreenPopOutService, EquipmentScreenPopOutService>();
        services.AddSingleton<EquipmentCommunicationMonitorViewModel>();
        services.AddSingleton<IEquipmentExchangeTraceSink>(provider =>
            provider.GetRequiredService<EquipmentCommunicationMonitorViewModel>());
    }

    private static void AddDesktopViewModels(this IServiceCollection services)
    {
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainPageViewModel>();
        services.AddSingleton<LiveInteractionPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
    }

    private static void AddDesktopViews(this IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainPage>();
        services.AddSingleton<LiveInteractionPage>();
        services.AddSingleton<SettingsPage>();
    }

    private static void CopyRealtimeVideo(
        RealtimeVideoOptions source,
        RealtimeVideoOptions destination)
    {
        var snapshot = (source ?? new RealtimeVideoOptions()).Clone();
        destination.Enabled = snapshot.Enabled;
        destination.SignalR = snapshot.SignalR;
        destination.Authentication = snapshot.Authentication;
        destination.Retry = snapshot.Retry;
        destination.Frames = snapshot.Frames;
    }
}
