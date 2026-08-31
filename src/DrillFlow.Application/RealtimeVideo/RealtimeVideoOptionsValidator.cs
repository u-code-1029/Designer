using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace DrillFlow.Application.RealtimeVideo;

public sealed class RealtimeVideoOptionsValidator : IValidateOptions<RealtimeVideoOptions>
{
    public const int MaximumSupportedFrameBytes = 64 * 1024 * 1024;
    public const int MaximumSupportedBufferCapacity = 8;
    public const int MaximumReconnectDelayCount = 20;

    public ValidateOptionsResult Validate(string? name, RealtimeVideoOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var failures = new List<string>();
        var signalR = options.SignalR;
        var authentication = options.Authentication;
        var retry = options.Retry;
        var frames = options.Frames;

        if (signalR is null)
        {
            failures.Add("SignalR settings are required.");
        }
        else
        {
            ValidateSignalR(options.Enabled, signalR, failures);
        }

        if (authentication is null)
        {
            failures.Add("Authentication settings are required.");
        }
        else
        {
            ValidateAuthentication(options.Enabled, authentication, failures);
        }

        if (retry is null)
        {
            failures.Add("Retry settings are required.");
        }
        else
        {
            ValidateRetry(retry, failures);
        }

        if (frames is null)
        {
            failures.Add("Frame settings are required.");
        }
        else
        {
            ValidateFrames(frames, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSignalR(
        bool enabled,
        RealtimeVideoSignalROptions options,
        ICollection<string> failures)
    {
        if (!Enum.IsDefined(typeof(RealtimeVideoTransport), options.Transport))
        {
            failures.Add("Realtime video transport has an unsupported value.");
        }

        if (!IsPositiveFinite(options.ServerTimeoutSeconds))
        {
            failures.Add("SignalR server timeout must be a positive finite number of seconds.");
        }

        if (!IsPositiveFinite(options.KeepAliveIntervalSeconds))
        {
            failures.Add("SignalR keep-alive interval must be a positive finite number of seconds.");
        }
        else if (IsPositiveFinite(options.ServerTimeoutSeconds)
                 && options.KeepAliveIntervalSeconds >= options.ServerTimeoutSeconds)
        {
            failures.Add("SignalR keep-alive interval must be shorter than the server timeout.");
        }

        if (!enabled)
        {
            return;
        }

        if (!TryValidateEndpoint(options.HubEndpoint))
        {
            failures.Add(
                "An enabled real-time video connection requires an absolute HTTP or HTTPS Hub endpoint without credentials, query, or fragment.");
        }

        if (string.IsNullOrWhiteSpace(options.StreamMethod)
            || options.StreamMethod.Length > 128)
        {
            failures.Add("An enabled real-time video connection requires a stream method of at most 128 characters.");
        }
    }

    private static void ValidateAuthentication(
        bool enabled,
        RealtimeVideoAuthenticationOptions options,
        ICollection<string> failures)
    {
        if (!Enum.IsDefined(typeof(RealtimeVideoAuthenticationMode), options.Mode))
        {
            failures.Add("Realtime video authentication mode has an unsupported value.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.TokenEnvironmentVariable)
            && !IsEnvironmentVariableName(options.TokenEnvironmentVariable))
        {
            failures.Add("JWT environment variable name contains unsupported characters.");
        }

        if (enabled
            && options.Mode == RealtimeVideoAuthenticationMode.Jwt
            && string.IsNullOrWhiteSpace(options.CredentialName)
            && string.IsNullOrWhiteSpace(options.TokenEnvironmentVariable))
        {
            failures.Add(
                "JWT authentication requires a protected credential name or token environment variable name.");
        }
    }

    private static void ValidateRetry(
        RealtimeVideoRetryOptions options,
        ICollection<string> failures)
    {
        if (options.InitialConnectMaximumAttempts < 1)
        {
            failures.Add("Initial SignalR connection attempts must be at least one.");
        }

        var delays = options.ReconnectDelaysSeconds;
        if (delays is null)
        {
            failures.Add("SignalR reconnect delays are required.");
            return;
        }

        if (delays.Length > MaximumReconnectDelayCount)
        {
            failures.Add($"SignalR reconnect delays may contain at most {MaximumReconnectDelayCount} values.");
        }

        if (options.Enabled && delays.Length == 0)
        {
            failures.Add("At least one reconnect delay is required when SignalR retry is enabled.");
        }

        for (var index = 0; index < delays.Length; index++)
        {
            if (!IsNonNegativeFinite(delays[index]))
            {
                failures.Add("Every SignalR reconnect delay must be a non-negative finite number of seconds.");
                break;
            }
        }
    }

    private static void ValidateFrames(
        RealtimeVideoFrameOptions options,
        ICollection<string> failures)
    {
        if (options.MaximumFrameBytes < 1
            || options.MaximumFrameBytes > MaximumSupportedFrameBytes)
        {
            failures.Add(
                $"Maximum frame bytes must be between 1 and {MaximumSupportedFrameBytes}.");
        }

        if (options.BufferCapacity < 1
            || options.BufferCapacity > MaximumSupportedBufferCapacity)
        {
            failures.Add(
                $"Frame buffer capacity must be between 1 and {MaximumSupportedBufferCapacity}.");
        }
    }

    private static bool TryValidateEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static bool IsEnvironmentVariableName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0
            || !(trimmed[0] == '_' || IsAsciiLetter(trimmed[0])))
        {
            return false;
        }

        for (var index = 1; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character != '_'
                && !IsAsciiLetter(character)
                && (character < '0' || character > '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';

    private static bool IsPositiveFinite(double value) =>
        value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsNonNegativeFinite(double value) =>
        value >= 0d && !double.IsNaN(value) && !double.IsInfinity(value);
}
