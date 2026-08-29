using System;
using System.IO;
using DrillFlow.Desktop.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace DrillFlow.Desktop.Services;

public sealed class UserSettingsStore : IUserSettingsStore
{
    private readonly DesignerOptions _defaults;
    private readonly string _settingsPath;

    public UserSettingsStore(IOptions<DesignerOptions> defaults)
    {
        _defaults = defaults.Value;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrillFlow",
            "settings.json");
    }

    public UserPreferences Load()
    {
        var fallback = new UserPreferences
        {
            Language = string.IsNullOrWhiteSpace(_defaults.Language) ? "Auto" : _defaults.Language,
            Theme = ThemeSelection.Normalize(_defaults.Theme),
            ValidateWorkflowOnEveryChange = _defaults.ValidateWorkflowOnEveryChange,
            Communication = (_defaults.Communication ?? new CommunicationSettings()).Clone()
        };

        try
        {
            if (!File.Exists(_settingsPath))
            {
                return fallback;
            }

            var root = JObject.Parse(File.ReadAllText(_settingsPath));
            var persisted = (root["DrillFlow"] ?? root).ToObject<UserPreferences>();
            if (persisted is null)
            {
                return fallback;
            }

            persisted.Communication ??= fallback.Communication;
            MigrateLegacyDefaultFileNames(persisted.Communication);
            persisted.Theme = ThemeSelection.Normalize(persisted.Theme);
            return persisted;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not load user settings from {SettingsPath}", _settingsPath);
            return fallback;
        }
    }

    public void Save(UserPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = _settingsPath + ".tmp";
        var root = new JObject
        {
            ["DrillFlow"] = JObject.FromObject(preferences)
        };
        File.WriteAllText(temporaryPath, root.ToString(Formatting.Indented));

        if (File.Exists(_settingsPath))
        {
            File.Replace(temporaryPath, _settingsPath, null);
        }
        else
        {
            File.Move(temporaryPath, _settingsPath);
        }
    }

    private static void MigrateLegacyDefaultFileNames(CommunicationSettings communication)
    {
        // Version 1 used this exact pair as its built-in defaults. Preserve every custom name,
        // while ensuring existing installations adopt the new XML wire contract automatically.
        if (string.Equals(communication.RequestFileName, "request.json", StringComparison.OrdinalIgnoreCase)
            && string.Equals(communication.ResponseFileName, "response.json", StringComparison.OrdinalIgnoreCase))
        {
            communication.RequestFileName = "request.xml";
            communication.ResponseFileName = "response.xml";
        }
    }
}
