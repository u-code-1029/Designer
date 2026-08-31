using System;
using System.IO;
using DrillFlow.Application.Communication;
using DrillFlow.Application.RealtimeVideo;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopUserSettingsStoreTests
{
    [Fact]
    public void LegacySettings_AreReadPreservedAndMigratedToCanonicalUserFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlowUserSettingsTests-" + Guid.NewGuid().ToString("N"));
        var canonicalPath = Path.Combine(directory, "appsettings.user.json");
        var legacyPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            var legacyText = "{\"DrillFlow\":{"
                             + "\"Language\":\"en-US\","
                             + "\"Communication\":{"
                             + "\"ExchangeFolder\":\"C:\\\\LegacyExchange\","
                             + "\"RetryIntervalMilliseconds\":1250},"
                             + "\"RealtimeVideo\":{"
                             + "\"Authentication\":{"
                             + "\"Mode\":\"Jwt\","
                             + "\"CredentialName\":\"DrillFlow/Video\","
                             + "\"TokenEnvironmentVariable\":\"DRILLFLOW_VIDEO_TOKEN\"}}}}";
            File.WriteAllText(legacyPath, legacyText);

            var defaults = new DesignerOptions
            {
                RealtimeVideo = new RealtimeVideoOptions()
            };
            var equipmentDefaults = new EquipmentCommunicationOptions
            {
                ExchangeDirectory = @"C:\DefaultExchange",
                RequestFileName = "deployment-request.xml"
            };
            var store = new UserSettingsStore(
                defaults,
                equipmentDefaults,
                canonicalPath,
                legacyPath);

            var loaded = store.Load();

            Assert.Equal("en-US", loaded.Language);
            Assert.Equal(@"C:\LegacyExchange", loaded.Communication.ExchangeFolder);
            Assert.Equal("deployment-request.xml", loaded.Communication.RequestFileName);
            Assert.Equal(1.25d, loaded.Communication.RetryDelaySeconds);
            Assert.Equal("DrillFlow/Video", loaded.RealtimeVideo.Authentication.CredentialName);
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(canonicalPath));
            Assert.Equal(legacyText, File.ReadAllText(legacyPath));

            var migrated = JObject.Parse(File.ReadAllText(canonicalPath));
            Assert.Null(migrated["DrillFlow"]?["Communication"]?["RetryIntervalMilliseconds"]);
            Assert.Equal(
                1.25d,
                (double)migrated["DrillFlow"]?["Communication"]?["RetryDelaySeconds"]!);
            Assert.Null(migrated["DrillFlow"]?["RealtimeVideo"]?["Authentication"]?["Token"]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void MissingUserFiles_FallBackToCanonicalEquipmentOptions()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlowUserSettingsTests-" + Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(
            new DesignerOptions(),
            new EquipmentCommunicationOptions
            {
                ExchangeDirectory = @"D:\EquipmentExchange",
                RequestFileName = "command.xml",
                StableReadDelay = TimeSpan.FromSeconds(0.125d)
            },
            Path.Combine(directory, "appsettings.user.json"),
            Path.Combine(directory, "settings.json"));

        var loaded = store.Load();

        Assert.Equal(@"D:\EquipmentExchange", loaded.Communication.ExchangeFolder);
        Assert.Equal("command.xml", loaded.Communication.RequestFileName);
        Assert.Equal(0.125d, loaded.Communication.StableReadDelaySeconds);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void UserFileWithoutCommunication_StillUsesDeploymentEquipmentDefaults()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlowUserSettingsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var canonicalPath = Path.Combine(directory, "appsettings.user.json");
            File.WriteAllText(
                canonicalPath,
                "{\"DrillFlow\":{\"Theme\":\"Dark\"}}");
            var store = new UserSettingsStore(
                new DesignerOptions(),
                new EquipmentCommunicationOptions
                {
                    ExchangeDirectory = @"C:\DeploymentExchange",
                    PollingInterval = TimeSpan.FromSeconds(0.375d)
                },
                canonicalPath,
                Path.Combine(directory, "settings.json"));

            var loaded = store.Load();

            Assert.Equal(ThemeSelection.Dark, loaded.Theme);
            Assert.Equal(@"C:\DeploymentExchange", loaded.Communication.ExchangeFolder);
            Assert.Equal(0.375d, loaded.Communication.PollingIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void PartialUserFile_PreservesDeploymentAppearanceAndValidationDefaults()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlowUserSettingsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var canonicalPath = Path.Combine(directory, "appsettings.user.json");
            File.WriteAllText(
                canonicalPath,
                "{\"DrillFlow\":{\"Communication\":{\"RequestFileName\":\"user-request.xml\"}}}");
            var store = new UserSettingsStore(
                new DesignerOptions
                {
                    Language = "en-US",
                    Theme = ThemeSelection.Dark,
                    ValidateWorkflowOnEveryChange = false
                },
                new EquipmentCommunicationOptions
                {
                    ExchangeDirectory = @"C:\DeploymentExchange"
                },
                canonicalPath,
                Path.Combine(directory, "settings.json"));

            var loaded = store.Load();

            Assert.Equal("en-US", loaded.Language);
            Assert.Equal(ThemeSelection.Dark, loaded.Theme);
            Assert.False(loaded.ValidateWorkflowOnEveryChange);
            Assert.Equal("user-request.xml", loaded.Communication.RequestFileName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void MalformedNestedGroup_DoesNotDiscardIndependentAppearanceSettings()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DrillFlowUserSettingsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var canonicalPath = Path.Combine(directory, "appsettings.user.json");
            File.WriteAllText(
                canonicalPath,
                "{\"DrillFlow\":{\"Language\":\"ko-KR\",\"Communication\":\"invalid\"}}");
            var store = new UserSettingsStore(
                new DesignerOptions(),
                new EquipmentCommunicationOptions
                {
                    ExchangeDirectory = @"D:\SafeExchange"
                },
                canonicalPath,
                Path.Combine(directory, "settings.json"));

            var loaded = store.Load();

            Assert.Equal("ko-KR", loaded.Language);
            Assert.Equal(@"D:\SafeExchange", loaded.Communication.ExchangeFolder);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
