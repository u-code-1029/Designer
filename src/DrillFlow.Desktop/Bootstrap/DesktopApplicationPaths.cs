using System;
using System.IO;

namespace DrillFlow.Desktop.Bootstrap;

/// <summary>
/// Resolves every host-level file location used before dependency injection is available.
/// Keeping these paths in one place prevents startup, logging, and settings persistence from
/// silently choosing different LocalAppData roots.
/// </summary>
internal sealed class DesktopApplicationPaths
{
    private DesktopApplicationPaths(
        string applicationBaseDirectory,
        string localApplicationDirectory)
    {
        ApplicationBaseDirectory = applicationBaseDirectory;
        LocalApplicationDirectory = localApplicationDirectory;
    }

    public string ApplicationBaseDirectory { get; }

    public string LocalApplicationDirectory { get; }

    public string LogDirectory => Path.Combine(LocalApplicationDirectory, "Logs");

    public string UserSettingsFile => Path.Combine(
        LocalApplicationDirectory,
        "appsettings.user.json");

    public string LegacyUserSettingsFile => Path.Combine(
        LocalApplicationDirectory,
        "settings.json");

    public static DesktopApplicationPaths CreateDefault()
    {
        var localApplicationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrillFlow");

        return new DesktopApplicationPaths(
            AppDomain.CurrentDomain.BaseDirectory,
            localApplicationDirectory);
    }
}
