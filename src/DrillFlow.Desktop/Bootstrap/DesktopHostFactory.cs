using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DrillFlow.Desktop.Bootstrap;

/// <summary>
/// Builds the Generic Host and defines configuration-provider precedence.
/// </summary>
internal static class DesktopHostFactory
{
    public static IHost Create(DesktopApplicationPaths paths)
    {
        return Host.CreateDefaultBuilder()
            .UseContentRoot(paths.ApplicationBaseDirectory)
            .ConfigureAppConfiguration(configuration =>
                ConfigureApplicationConfiguration(
                    configuration,
                    paths.ApplicationBaseDirectory))
            .UseSerilog((_, _, logger) => DesktopLogging.ConfigureHostLogger(logger, paths))
            .ConfigureServices((context, services) =>
                services.AddDrillFlowDesktop(
                    context.Configuration,
                    paths.UserSettingsFile,
                    paths.LegacyUserSettingsFile))
            .Build();
    }

    internal static void ConfigureApplicationConfiguration(
        IConfigurationBuilder configuration,
        string applicationBaseDirectory)
    {
        // Host.CreateDefaultBuilder includes every unprefixed process environment variable in
        // application IConfiguration. Clear those default application providers before adding
        // the two explicit sources below so DRILLFLOW_SIGNALR_JWT and other secrets cannot be
        // surfaced by configuration enumeration or diagnostics.
        configuration.Sources.Clear();
        configuration.SetBasePath(applicationBaseDirectory);
        configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        // Only non-secret deployment overrides belong in IConfiguration. Example:
        // DRILLFLOW_CONFIG_EquipmentCommunication__ResponseTimeout.
        configuration.AddEnvironmentVariables("DRILLFLOW_CONFIG_");
    }
}
