using System;
using System.Collections.Generic;
using System.IO;
using DrillFlow.Application.RealtimeVideo;
using DrillFlow.Desktop.Bootstrap;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopStartupSettingsLoaderTests
{
    [Fact]
    public void HostConfiguration_ExcludesUnprefixedSecretsAndKeepsPrefixedOverrides()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var secretVariable = "DRILLFLOW_SIGNALR_JWT_TEST_" + suffix;
        var overrideVariable = "DRILLFLOW_CONFIG_SecurityProbe__" + suffix;
        var overrideKey = "SecurityProbe:" + suffix;
        var previousSecret = Environment.GetEnvironmentVariable(secretVariable);
        var previousOverride = Environment.GetEnvironmentVariable(overrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(secretVariable, "must-not-enter-configuration");
            Environment.SetEnvironmentVariable(overrideVariable, "expected-override");
            var builder = new ConfigurationBuilder()
                .AddEnvironmentVariables();

            DesktopHostFactory.ConfigureApplicationConfiguration(
                builder,
                Path.GetTempPath());
            var configuration = builder.Build();

            Assert.Null(configuration[secretVariable]);
            Assert.Equal("expected-override", configuration[overrideKey]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVariable, previousSecret);
            Environment.SetEnvironmentVariable(overrideVariable, previousOverride);
        }
    }

    [Fact]
    public void CanonicalUserFile_WinsOverLegacyAndKeepsValidOverrides()
    {
        using var directory = new StartupSettingsTestDirectory();
        var canonical = Path.Combine(directory.Path, "appsettings.user.json");
        var legacy = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            canonical,
            "{\"DrillFlow\":{" +
            "\"Language\":\"en-US\"," +
            "\"Communication\":{" +
            "\"ExchangeFolder\":\"C:\\\\Equipment\"," +
            "\"LiveImageFolder\":\"C:\\\\Equipment\\\\Live\"}," +
            "\"RealtimeVideo\":{\"Enabled\":false}}}");
        File.WriteAllText(legacy, "{\"DrillFlow\":{\"Language\":\"ko-KR\"}}");

        var loaded = StartupSettingsLoader.Load(CreateDefaults(), canonical, legacy);

        Assert.Equal("en-US", loaded.Language);
        Assert.NotNull(loaded.Communication);
        Assert.Equal(@"C:\Equipment", loaded.Communication!.ExchangeFolder);
        Assert.Equal("deployment-request.xml", loaded.Communication.RequestFileName);
        Assert.False(loaded.RealtimeVideo.Enabled);
        Assert.Equal("ConfiguredFrames", loaded.RealtimeVideo.SignalR.StreamMethod);
    }

    [Fact]
    public void LegacyDefaultJsonFileNames_AreMigratedBeforeFirstExchange()
    {
        using var directory = new StartupSettingsTestDirectory();
        var legacy = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            legacy,
            "{\"DrillFlow\":{\"Communication\":{" +
            "\"RequestFileName\":\"request.json\"," +
            "\"ResponseFileName\":\"response.json\"}}}");

        var loaded = StartupSettingsLoader.Load(
            CreateDefaults(),
            Path.Combine(directory.Path, "appsettings.user.json"),
            legacy);

        Assert.NotNull(loaded.Communication);
        Assert.Equal("request.xml", loaded.Communication!.RequestFileName);
        Assert.Equal("response.xml", loaded.Communication.ResponseFileName);
    }

    [Fact]
    public void InvalidIndependentGroups_FallBackWithoutDiscardingAppearance()
    {
        using var directory = new StartupSettingsTestDirectory();
        var canonical = Path.Combine(directory.Path, "appsettings.user.json");
        File.WriteAllText(
            canonical,
            "{\"DrillFlow\":{" +
            "\"Language\":\"ko-KR\"," +
            "\"Theme\":\"Dark\"," +
            "\"Communication\":{\"ExchangeFolder\":\"relative\"}," +
            "\"RealtimeVideo\":{" +
            "\"Enabled\":true," +
            "\"SignalR\":{\"HubEndpoint\":\"not-a-uri\"}}}}");

        var loaded = StartupSettingsLoader.Load(
            CreateDefaults(),
            canonical,
            Path.Combine(directory.Path, "settings.json"));

        Assert.Equal("ko-KR", loaded.Language);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Null(loaded.Communication);
        Assert.False(loaded.RealtimeVideo.Enabled);
        Assert.Equal("ConfiguredFrames", loaded.RealtimeVideo.SignalR.StreamMethod);
        Assert.Equal(
            RealtimeVideoTransport.LongPolling,
            loaded.RealtimeVideo.SignalR.Transport);
    }

    [Fact]
    public void MissingUserFiles_UseDeploymentDefaultsWithoutCommunicationOverride()
    {
        using var directory = new StartupSettingsTestDirectory();

        var loaded = StartupSettingsLoader.Load(
            CreateDefaults(),
            Path.Combine(directory.Path, "appsettings.user.json"),
            Path.Combine(directory.Path, "settings.json"));

        Assert.Equal("Auto", loaded.Language);
        Assert.Null(loaded.Communication);
        Assert.False(loaded.RealtimeVideo.Enabled);
        Assert.Equal("ConfiguredFrames", loaded.RealtimeVideo.SignalR.StreamMethod);
    }

    [Fact]
    public void MalformedNestedValues_FallBackPerGroupWithoutDiscardingAppearance()
    {
        using var directory = new StartupSettingsTestDirectory();
        var canonical = Path.Combine(directory.Path, "appsettings.user.json");
        File.WriteAllText(
            canonical,
            "{\"DrillFlow\":{" +
            "\"Language\":\"ko-KR\"," +
            "\"Theme\":\"Dark\"," +
            "\"Communication\":{\"ResponseTimeoutSeconds\":\"not-a-number\"}," +
            "\"RealtimeVideo\":{\"SignalR\":\"not-an-object\"}}}");

        var loaded = StartupSettingsLoader.Load(
            CreateDefaults(),
            canonical,
            Path.Combine(directory.Path, "settings.json"));

        Assert.Equal("ko-KR", loaded.Language);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Null(loaded.Communication);
        Assert.False(loaded.RealtimeVideo.Enabled);
        Assert.Equal("ConfiguredFrames", loaded.RealtimeVideo.SignalR.StreamMethod);
    }

    [Fact]
    public void MalformedCommunication_DoesNotDiscardValidRealtimeOverride()
    {
        using var directory = new StartupSettingsTestDirectory();
        var canonical = Path.Combine(directory.Path, "appsettings.user.json");
        File.WriteAllText(
            canonical,
            "{\"DrillFlow\":{" +
            "\"Communication\":{\"ResponseTimeoutSeconds\":\"not-a-number\"}," +
            "\"RealtimeVideo\":{\"SignalR\":{\"StreamMethod\":\"UserFrames\"}}}}");

        var loaded = StartupSettingsLoader.Load(
            CreateDefaults(),
            canonical,
            Path.Combine(directory.Path, "settings.json"));

        Assert.Null(loaded.Communication);
        Assert.Equal("UserFrames", loaded.RealtimeVideo.SignalR.StreamMethod);
    }

    [Fact]
    public void MalformedRealtime_DoesNotDiscardValidCommunicationOverride()
    {
        using var directory = new StartupSettingsTestDirectory();
        var canonical = Path.Combine(directory.Path, "appsettings.user.json");
        File.WriteAllText(
            canonical,
            "{\"DrillFlow\":{" +
            "\"Communication\":{\"ExchangeFolder\":\"C:\\\\UserExchange\"}," +
            "\"RealtimeVideo\":{\"SignalR\":\"not-an-object\"}}}");

        var loaded = StartupSettingsLoader.Load(
            CreateDefaults(),
            canonical,
            Path.Combine(directory.Path, "settings.json"));

        Assert.NotNull(loaded.Communication);
        Assert.Equal(@"C:\UserExchange", loaded.Communication!.ExchangeFolder);
        Assert.Equal("ConfiguredFrames", loaded.RealtimeVideo.SignalR.StreamMethod);
    }

    private static IConfiguration CreateDefaults()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DrillFlow:Language"] = "Auto",
                ["DrillFlow:Theme"] = "System",
                ["DrillFlow:ValidateWorkflowOnEveryChange"] = "true",
                ["DrillFlow:RealtimeVideo:Enabled"] = "false",
                ["DrillFlow:RealtimeVideo:SignalR:Transport"] = "LongPolling",
                ["DrillFlow:RealtimeVideo:SignalR:StreamMethod"] = "ConfiguredFrames",
                ["EquipmentCommunication:ExchangeDirectory"] = @"C:\DeploymentExchange",
                ["EquipmentCommunication:RequestFileName"] = "deployment-request.xml"
            })
            .Build();
    }

    private sealed class StartupSettingsTestDirectory : IDisposable
    {
        public StartupSettingsTestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DrillFlowStartupSettingsTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
