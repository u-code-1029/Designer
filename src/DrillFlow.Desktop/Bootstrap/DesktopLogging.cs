using System.IO;
using Serilog;

namespace DrillFlow.Desktop.Bootstrap;

/// <summary>
/// Owns the two-stage Serilog setup: an early bootstrap logger and the Generic Host logger.
/// </summary>
internal static class DesktopLogging
{
    public static void ConfigureBootstrapLogger(DesktopApplicationPaths paths)
    {
        Directory.CreateDirectory(paths.LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }

    public static void ConfigureHostLogger(
        LoggerConfiguration logger,
        DesktopApplicationPaths paths)
    {
        logger
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "drillflow-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true);
    }
}
