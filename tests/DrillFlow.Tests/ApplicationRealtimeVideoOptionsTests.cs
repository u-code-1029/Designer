using System;
using DrillFlow.Application.RealtimeVideo;
using DrillFlow.Desktop.ViewModels;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationRealtimeVideoOptionsTests
{
    private readonly RealtimeVideoOptionsValidator _validator = new();

    [Fact]
    public void DisabledDefaults_AreValidAndUseWindows7CompatibleTransport()
    {
        var options = new RealtimeVideoOptions();

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.False(options.Enabled);
        Assert.Equal(RealtimeVideoTransport.LongPolling, options.SignalR.Transport);
        Assert.Equal(1, options.Frames.BufferCapacity);
    }

    [Fact]
    public void EnabledConnection_RequiresSafeEndpointAndJwtReference()
    {
        var options = new RealtimeVideoOptions
        {
            Enabled = true,
            SignalR = new RealtimeVideoSignalROptions
            {
                HubEndpoint = "https://user:secret@example.test/hub?token=secret",
                StreamMethod = "StreamFrames"
            },
            Authentication = new RealtimeVideoAuthenticationOptions
            {
                Mode = RealtimeVideoAuthenticationMode.Jwt,
                CredentialName = string.Empty,
                TokenEnvironmentVariable = string.Empty
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("JWT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidRetryAndFrameBounds_AreRejected()
    {
        var options = new RealtimeVideoOptions
        {
            Retry = new RealtimeVideoRetryOptions
            {
                Enabled = true,
                InitialConnectMaximumAttempts = 0,
                ReconnectDelaysSeconds = new[] { 0d, double.PositiveInfinity }
            },
            Frames = new RealtimeVideoFrameOptions
            {
                MaximumFrameBytes = RealtimeVideoOptionsValidator.MaximumSupportedFrameBytes + 1,
                BufferCapacity = 0
            }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("attempt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("delay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("frame bytes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, failure => failure.Contains("buffer capacity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JsonRoundTrip_PersistsOnlyJwtReferencesNotASecretValue()
    {
        var source = new RealtimeVideoOptions
        {
            Enabled = true,
            SignalR = new RealtimeVideoSignalROptions
            {
                HubEndpoint = "https://equipment.example.test/video",
                StreamMethod = "Frames",
                Transport = RealtimeVideoTransport.LongPolling,
                ServerTimeoutSeconds = 45.5d,
                KeepAliveIntervalSeconds = 10d
            },
            Authentication = new RealtimeVideoAuthenticationOptions
            {
                Mode = RealtimeVideoAuthenticationMode.Jwt,
                CredentialName = "DrillFlow/EquipmentVideo",
                TokenEnvironmentVariable = "DRILLFLOW_VIDEO_TOKEN"
            }
        };

        var json = JObject.FromObject(source);
        var roundTripped = json.ToObject<RealtimeVideoOptions>();

        Assert.NotNull(roundTripped);
        Assert.True(RealtimeVideoSettingsViewModel.AreEquivalent(source, roundTripped!));
        Assert.Equal("DrillFlow/EquipmentVideo", (string?)json["Authentication"]?["CredentialName"]);
        Assert.Equal("DRILLFLOW_VIDEO_TOKEN", (string?)json["Authentication"]?["TokenEnvironmentVariable"]);
        Assert.Null(json["Authentication"]?["Token"]);
        Assert.Null(json["Authentication"]?["Jwt"]);
        Assert.DoesNotContain("secret-token-value", json.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EditableDraft_ParsesInvariantSecondListsAndEnums()
    {
        var draft = new RealtimeVideoSettingsViewModel
        {
            Enabled = true,
            HubEndpoint = "https://equipment.example.test/video",
            StreamMethod = "StreamFrames",
            Transport = "longpolling",
            ServerTimeoutSeconds = "30.5",
            KeepAliveIntervalSeconds = "5",
            AuthenticationMode = "jwt",
            TokenEnvironmentVariable = "DRILLFLOW_SIGNALR_JWT",
            RetryEnabled = true,
            InitialConnectMaximumAttempts = "4",
            ReconnectDelaysSeconds = "0, 0.5; 2 10",
            MaximumFrameBytes = "1048576",
            BufferCapacity = "1"
        };

        var parsed = draft.TryBuild(out var options);

        Assert.True(parsed);
        Assert.Equal(RealtimeVideoTransport.LongPolling, options.SignalR.Transport);
        Assert.Equal(new[] { 0d, 0.5d, 2d, 10d }, options.Retry.ReconnectDelaysSeconds);
        Assert.True(_validator.Validate(null, options).Succeeded);
    }
}
