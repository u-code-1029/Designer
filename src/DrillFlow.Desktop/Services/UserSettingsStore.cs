using System;
using System.IO;
using DrillFlow.Desktop.Models;
using DrillFlow.Application.Communication;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace DrillFlow.Desktop.Services;

public sealed class UserSettingsStore : IUserSettingsStore
{
    private readonly DesignerOptions _defaults;
    private readonly EquipmentCommunicationOptions _communicationDefaults;
    private readonly string _settingsPath;
    private readonly string _legacySettingsPath;

    public UserSettingsStore(
        IOptions<DesignerOptions> defaults,
        IOptions<EquipmentCommunicationOptions> communicationDefaults)
        : this(
            defaults?.Value ?? throw new ArgumentNullException(nameof(defaults)),
            communicationDefaults?.Value
            ?? throw new ArgumentNullException(nameof(communicationDefaults)),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrillFlow",
                "appsettings.user.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrillFlow",
                "settings.json"))
    {
    }

    internal UserSettingsStore(
        DesignerOptions defaults,
        EquipmentCommunicationOptions communicationDefaults,
        string settingsPath,
        string legacySettingsPath)
    {
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _communicationDefaults = communicationDefaults
            ?? throw new ArgumentNullException(nameof(communicationDefaults));
        _settingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? throw new ArgumentException("A user settings path is required.", nameof(settingsPath))
            : Path.GetFullPath(settingsPath);
        _legacySettingsPath = string.IsNullOrWhiteSpace(legacySettingsPath)
            ? throw new ArgumentException(
                "A legacy user settings path is required.",
                nameof(legacySettingsPath))
            : Path.GetFullPath(legacySettingsPath);
    }

    public UserPreferences Load()
    {
        var fallback = new UserPreferences
        {
            Language = string.IsNullOrWhiteSpace(_defaults.Language) ? "Auto" : _defaults.Language,
            Theme = ThemeSelection.Normalize(_defaults.Theme),
            ValidateWorkflowOnEveryChange = _defaults.ValidateWorkflowOnEveryChange,
            Communication = CommunicationSettings.FromOptions(_communicationDefaults),
            RealtimeVideo = (_defaults.RealtimeVideo ?? new DrillFlow.Application.RealtimeVideo.RealtimeVideoOptions()).Clone()
        };

        try
        {
            var sourcePath = File.Exists(_settingsPath)
                ? _settingsPath
                : File.Exists(_legacySettingsPath)
                    ? _legacySettingsPath
                    : string.Empty;
            if (sourcePath.Length == 0)
            {
                return fallback;
            }

            var root = JObject.Parse(File.ReadAllText(sourcePath));
            var settingsObject = root["DrillFlow"] as JObject ?? root;
            if (settingsObject.Type != JTokenType.Object)
            {
                return fallback;
            }

            var persistedLanguage = settingsObject.Value<string>(nameof(UserPreferences.Language));
            var persistedTheme = settingsObject.Value<string>(nameof(UserPreferences.Theme));
            var persistedValidation = settingsObject.Value<bool?>(
                nameof(UserPreferences.ValidateWorkflowOnEveryChange));
            var persisted = new UserPreferences
            {
                Language = string.IsNullOrWhiteSpace(persistedLanguage)
                    ? fallback.Language
                    : persistedLanguage!,
                Theme = string.IsNullOrWhiteSpace(persistedTheme)
                    ? fallback.Theme
                    : persistedTheme!,
                ValidateWorkflowOnEveryChange = persistedValidation
                    ?? fallback.ValidateWorkflowOnEveryChange,
                // Merge only the members present in each nested group. An older or hand-edited
                // partial file therefore cannot reset newly introduced deployment settings.
                Communication = MergeSettings(
                    fallback.Communication,
                    settingsObject["Communication"]),
                RealtimeVideo = MergeSettings(
                    fallback.RealtimeVideo,
                    settingsObject["RealtimeVideo"])
            };
            persisted.Communication.MigrateLegacyDefaultFileNames();
            persisted.Theme = ThemeSelection.Normalize(persisted.Theme);
            if (string.Equals(sourcePath, _legacySettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                TryPersistLegacyMigration(persisted);
            }

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
        try
        {
            if (File.Exists(_settingsPath))
            {
                File.Replace(temporaryPath, _settingsPath, null);
            }
            else
            {
                File.Move(temporaryPath, _settingsPath);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static T MergeSettings<T>(T fallback, JToken? persisted)
        where T : class
    {
        var baseline = CloneSettings(fallback);
        if (persisted is null || persisted.Type == JTokenType.Null)
        {
            return baseline;
        }

        if (persisted.Type != JTokenType.Object)
        {
            Log.Warning(
                "Ignored persisted {SettingsType} settings because the JSON value is not an object",
                typeof(T).Name);
            return baseline;
        }

        try
        {
            var merged = CloneSettings(fallback);
            using var reader = persisted.CreateReader();
            JsonSerializer.CreateDefault().Populate(reader, merged);
            return merged;
        }
        catch (Exception exception) when (IsSettingsDeserializationFailure(exception))
        {
            Log.Warning(
                exception,
                "Ignored invalid persisted {SettingsType} settings group",
                typeof(T).Name);
            return baseline;
        }
    }

    private static bool IsSettingsDeserializationFailure(Exception exception) =>
        exception is JsonException
        || exception is FormatException
        || exception is InvalidCastException
        || exception is OverflowException
        || exception is ArgumentException;

    private static T CloneSettings<T>(T settings)
        where T : class
    {
        return settings switch
        {
            CommunicationSettings communication => (T)(object)communication.Clone(),
            DrillFlow.Application.RealtimeVideo.RealtimeVideoOptions realtime =>
                (T)(object)realtime.Clone(),
            _ => throw new NotSupportedException(
                $"Settings merge is not supported for {typeof(T).FullName}.")
        };
    }

    private void TryPersistLegacyMigration(UserPreferences preferences)
    {
        try
        {
            Save(preferences);
            Log.Information(
                "Migrated legacy user settings from {LegacySettingsPath} to {SettingsPath}; the legacy file was preserved as a read-only backup source",
                _legacySettingsPath,
                _settingsPath);
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "Could not migrate legacy user settings from {LegacySettingsPath} to {SettingsPath}",
                _legacySettingsPath,
                _settingsPath);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
