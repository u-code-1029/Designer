using System;
using System.IO;
using DrillFlow.Application.Communication;
using DrillFlow.Application.RealtimeVideo;
using DrillFlow.Desktop.Models;
using DrillFlow.Infrastructure.Communication;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace DrillFlow.Desktop.Bootstrap;

/// <summary>
/// Reads the persisted user override before the Host creates singleton options consumers.
/// Invalid communication values are rejected as one unit so a partially valid settings file
/// can never produce a mixed request/response exchange configuration.
/// </summary>
internal static class StartupSettingsLoader
{
    public static DesignerOptions Load(
        IConfiguration configuration,
        string userSettingsPath,
        string legacyUserSettingsPath)
    {
        var fallback = configuration.GetSection("DrillFlow").Get<DesignerOptions>()
            ?? new DesignerOptions();

        try
        {
            var sourcePath = File.Exists(userSettingsPath)
                ? userSettingsPath
                : legacyUserSettingsPath;
            if (!File.Exists(sourcePath))
            {
                return fallback;
            }

            var root = JObject.Parse(File.ReadAllText(sourcePath));
            var settingsObject = root["DrillFlow"] as JObject ?? root;
            if (settingsObject.Type != JTokenType.Object)
            {
                return fallback;
            }

            CommunicationSettings? communicationOverride = null;
            if (settingsObject["Communication"] is JObject communicationObject)
            {
                var deploymentCommunication = configuration
                    .GetSection(EquipmentCommunicationOptions.SectionName)
                    .Get<EquipmentCommunicationOptions>()
                    ?? new EquipmentCommunicationOptions();
                var candidateCommunication = CommunicationSettings.FromOptions(
                    deploymentCommunication);
                if (TryPopulate(communicationObject, candidateCommunication, "communication"))
                {
                    candidateCommunication.MigrateLegacyDefaultFileNames();
                    communicationOverride = candidateCommunication;
                }
            }

            var realtimeOverride = (fallback.RealtimeVideo ?? new RealtimeVideoOptions()).Clone();
            if (settingsObject["RealtimeVideo"] is JObject realtimeObject)
            {
                if (!TryPopulate(realtimeObject, realtimeOverride, "real-time video"))
                {
                    realtimeOverride = (fallback.RealtimeVideo ?? new RealtimeVideoOptions())
                        .Clone();
                }
            }

            var persistedLanguage = settingsObject.Value<string>(nameof(UserPreferences.Language));
            var persistedTheme = settingsObject.Value<string>(nameof(UserPreferences.Theme));
            var persistedValidation = settingsObject.Value<bool?>(
                nameof(UserPreferences.ValidateWorkflowOnEveryChange));

            var candidate = new DesignerOptions
            {
                Language = string.IsNullOrWhiteSpace(persistedLanguage)
                    ? fallback.Language
                    : persistedLanguage!,
                Theme = string.IsNullOrWhiteSpace(persistedTheme)
                    ? ThemeSelection.Normalize(fallback.Theme)
                    : ThemeSelection.Normalize(persistedTheme),
                ValidateWorkflowOnEveryChange = persistedValidation
                    ?? fallback.ValidateWorkflowOnEveryChange,
                Communication = communicationOverride,
                RealtimeVideo = realtimeOverride
            };

            if (candidate.Communication is not null)
            {
                var communicationOptions = new EquipmentCommunicationOptions();
                candidate.Communication.ApplyTo(communicationOptions);
                var validation = new EquipmentCommunicationOptionsValidator()
                    .Validate(null, communicationOptions);
                if (validation.Failed)
                {
                    Log.Warning(
                        "Persisted communication settings are invalid and will not be applied at startup: {Failures}",
                        string.Join("; ", validation.Failures));
                    candidate.Communication = null;
                }
            }

            var realtimeValidation = new RealtimeVideoOptionsValidator()
                .Validate(null, candidate.RealtimeVideo);
            if (realtimeValidation.Failed)
            {
                Log.Warning(
                    "Persisted real-time video settings are invalid and will not be applied at startup: {Failures}",
                    string.Join("; ", realtimeValidation.Failures));
                candidate.RealtimeVideo = (fallback.RealtimeVideo ?? new RealtimeVideoOptions())
                    .Clone();
            }

            return candidate;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not apply persisted settings during startup");
            return fallback;
        }
    }

    private static bool TryPopulate(
        JObject source,
        object destination,
        string settingsGroup)
    {
        try
        {
            using var reader = source.CreateReader();
            JsonSerializer.CreateDefault().Populate(reader, destination);
            return true;
        }
        catch (Exception exception) when (IsSettingsDeserializationFailure(exception))
        {
            Log.Warning(
                exception,
                "Could not deserialize persisted {SettingsGroup} settings; deployment defaults will be used for that group",
                settingsGroup);
            return false;
        }
    }

    private static bool IsSettingsDeserializationFailure(Exception exception) =>
        exception is JsonException
        || exception is FormatException
        || exception is InvalidCastException
        || exception is OverflowException
        || exception is ArgumentException;
}
