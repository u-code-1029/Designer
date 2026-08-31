using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DrillFlow.Application.RealtimeVideo;

namespace DrillFlow.Desktop.ViewModels;

/// <summary>
/// Editable, text-oriented representation of the strongly typed real-time video options.
/// Keeping parsing here leaves the Settings page coordinator focused on persistence and apply
/// policy while preserving ordinary WPF two-way bindings.
/// </summary>
public sealed class RealtimeVideoSettingsViewModel : ObservableObject
{
    private bool _enabled;
    private string _hubEndpoint = string.Empty;
    private string _streamMethod = "StreamFrames";
    private string _transport = nameof(RealtimeVideoTransport.LongPolling);
    private string _serverTimeoutSeconds = "30";
    private string _keepAliveIntervalSeconds = "15";
    private string _authenticationMode = nameof(RealtimeVideoAuthenticationMode.None);
    private string _credentialName = string.Empty;
    private string _tokenEnvironmentVariable = "DRILLFLOW_SIGNALR_JWT";
    private bool _retryEnabled = true;
    private string _initialConnectMaximumAttempts = "5";
    private string _reconnectDelaysSeconds = "0, 2, 10, 30";
    private string _maximumFrameBytes = "8388608";
    private string _bufferCapacity = "1";

    public string[] TransportChoices { get; } =
        Enum.GetNames(typeof(RealtimeVideoTransport));

    public string[] AuthenticationModeChoices { get; } =
        Enum.GetNames(typeof(RealtimeVideoAuthenticationMode));

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string HubEndpoint
    {
        get => _hubEndpoint;
        set => SetProperty(ref _hubEndpoint, value ?? string.Empty);
    }

    public string StreamMethod
    {
        get => _streamMethod;
        set => SetProperty(ref _streamMethod, value ?? string.Empty);
    }

    public string Transport
    {
        get => _transport;
        set => SetProperty(ref _transport, value ?? string.Empty);
    }

    public string ServerTimeoutSeconds
    {
        get => _serverTimeoutSeconds;
        set => SetProperty(ref _serverTimeoutSeconds, value ?? string.Empty);
    }

    public string KeepAliveIntervalSeconds
    {
        get => _keepAliveIntervalSeconds;
        set => SetProperty(ref _keepAliveIntervalSeconds, value ?? string.Empty);
    }

    public string AuthenticationMode
    {
        get => _authenticationMode;
        set => SetProperty(ref _authenticationMode, value ?? string.Empty);
    }

    public string CredentialName
    {
        get => _credentialName;
        set => SetProperty(ref _credentialName, value ?? string.Empty);
    }

    public string TokenEnvironmentVariable
    {
        get => _tokenEnvironmentVariable;
        set => SetProperty(ref _tokenEnvironmentVariable, value ?? string.Empty);
    }

    public bool RetryEnabled
    {
        get => _retryEnabled;
        set => SetProperty(ref _retryEnabled, value);
    }

    public string InitialConnectMaximumAttempts
    {
        get => _initialConnectMaximumAttempts;
        set => SetProperty(ref _initialConnectMaximumAttempts, value ?? string.Empty);
    }

    public string ReconnectDelaysSeconds
    {
        get => _reconnectDelaysSeconds;
        set => SetProperty(ref _reconnectDelaysSeconds, value ?? string.Empty);
    }

    public string MaximumFrameBytes
    {
        get => _maximumFrameBytes;
        set => SetProperty(ref _maximumFrameBytes, value ?? string.Empty);
    }

    public string BufferCapacity
    {
        get => _bufferCapacity;
        set => SetProperty(ref _bufferCapacity, value ?? string.Empty);
    }

    public void Load(RealtimeVideoOptions? options)
    {
        var source = options ?? new RealtimeVideoOptions();
        var signalR = source.SignalR ?? new RealtimeVideoSignalROptions();
        var authentication = source.Authentication ?? new RealtimeVideoAuthenticationOptions();
        var retry = source.Retry ?? new RealtimeVideoRetryOptions();
        var frames = source.Frames ?? new RealtimeVideoFrameOptions();

        Enabled = source.Enabled;
        HubEndpoint = signalR.HubEndpoint;
        StreamMethod = signalR.StreamMethod;
        Transport = signalR.Transport.ToString();
        ServerTimeoutSeconds = FormatDouble(signalR.ServerTimeoutSeconds);
        KeepAliveIntervalSeconds = FormatDouble(signalR.KeepAliveIntervalSeconds);
        AuthenticationMode = authentication.Mode.ToString();
        CredentialName = authentication.CredentialName;
        TokenEnvironmentVariable = authentication.TokenEnvironmentVariable;
        RetryEnabled = retry.Enabled;
        InitialConnectMaximumAttempts = retry.InitialConnectMaximumAttempts.ToString(
            CultureInfo.InvariantCulture);
        ReconnectDelaysSeconds = string.Join(
            ", ",
            (retry.ReconnectDelaysSeconds ?? Array.Empty<double>()).Select(FormatDouble));
        MaximumFrameBytes = frames.MaximumFrameBytes.ToString(CultureInfo.InvariantCulture);
        BufferCapacity = frames.BufferCapacity.ToString(CultureInfo.InvariantCulture);
    }

    public bool TryBuild(out RealtimeVideoOptions options)
    {
        options = new RealtimeVideoOptions();
        if (!Enum.TryParse(Transport, true, out RealtimeVideoTransport transport)
            || !Enum.TryParse(
                AuthenticationMode,
                true,
                out RealtimeVideoAuthenticationMode authenticationMode)
            || !TryParseFiniteDouble(ServerTimeoutSeconds, out var serverTimeout)
            || !TryParseFiniteDouble(KeepAliveIntervalSeconds, out var keepAlive)
            || !int.TryParse(
                InitialConnectMaximumAttempts,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var initialAttempts)
            || !TryParseDoubleList(ReconnectDelaysSeconds, out var reconnectDelays)
            || !int.TryParse(
                MaximumFrameBytes,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maximumFrameBytes)
            || !int.TryParse(
                BufferCapacity,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var bufferCapacity))
        {
            return false;
        }

        options = new RealtimeVideoOptions
        {
            Enabled = Enabled,
            SignalR = new RealtimeVideoSignalROptions
            {
                HubEndpoint = HubEndpoint.Trim(),
                StreamMethod = StreamMethod.Trim(),
                Transport = transport,
                ServerTimeoutSeconds = serverTimeout,
                KeepAliveIntervalSeconds = keepAlive
            },
            Authentication = new RealtimeVideoAuthenticationOptions
            {
                Mode = authenticationMode,
                CredentialName = CredentialName.Trim(),
                TokenEnvironmentVariable = TokenEnvironmentVariable.Trim()
            },
            Retry = new RealtimeVideoRetryOptions
            {
                Enabled = RetryEnabled,
                InitialConnectMaximumAttempts = initialAttempts,
                ReconnectDelaysSeconds = reconnectDelays
            },
            Frames = new RealtimeVideoFrameOptions
            {
                MaximumFrameBytes = maximumFrameBytes,
                BufferCapacity = bufferCapacity
            }
        };
        return true;
    }

    internal static bool AreEquivalent(
        RealtimeVideoOptions left,
        RealtimeVideoOptions right)
    {
        var leftSignalR = left.SignalR ?? new RealtimeVideoSignalROptions();
        var rightSignalR = right.SignalR ?? new RealtimeVideoSignalROptions();
        var leftAuthentication = left.Authentication ?? new RealtimeVideoAuthenticationOptions();
        var rightAuthentication = right.Authentication ?? new RealtimeVideoAuthenticationOptions();
        var leftRetry = left.Retry ?? new RealtimeVideoRetryOptions();
        var rightRetry = right.Retry ?? new RealtimeVideoRetryOptions();
        var leftFrames = left.Frames ?? new RealtimeVideoFrameOptions();
        var rightFrames = right.Frames ?? new RealtimeVideoFrameOptions();

        return left.Enabled == right.Enabled
               && string.Equals(
                   leftSignalR.HubEndpoint,
                   rightSignalR.HubEndpoint,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   leftSignalR.StreamMethod,
                   rightSignalR.StreamMethod,
                   StringComparison.Ordinal)
               && leftSignalR.Transport == rightSignalR.Transport
               && leftSignalR.ServerTimeoutSeconds.Equals(rightSignalR.ServerTimeoutSeconds)
               && leftSignalR.KeepAliveIntervalSeconds.Equals(rightSignalR.KeepAliveIntervalSeconds)
               && leftAuthentication.Mode == rightAuthentication.Mode
               && string.Equals(
                   leftAuthentication.CredentialName,
                   rightAuthentication.CredentialName,
                   StringComparison.Ordinal)
               && string.Equals(
                   leftAuthentication.TokenEnvironmentVariable,
                   rightAuthentication.TokenEnvironmentVariable,
                   StringComparison.Ordinal)
               && leftRetry.Enabled == rightRetry.Enabled
               && leftRetry.InitialConnectMaximumAttempts == rightRetry.InitialConnectMaximumAttempts
               && (leftRetry.ReconnectDelaysSeconds ?? Array.Empty<double>()).SequenceEqual(
                   rightRetry.ReconnectDelaysSeconds ?? Array.Empty<double>())
               && leftFrames.MaximumFrameBytes == rightFrames.MaximumFrameBytes
               && leftFrames.BufferCapacity == rightFrames.BufferCapacity;
    }

    private static bool TryParseFiniteDouble(string value, out double result)
    {
        return double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out result)
               && !double.IsNaN(result)
               && !double.IsInfinity(result);
    }

    private static bool TryParseDoubleList(string value, out double[] values)
    {
        var parts = (value ?? string.Empty).Split(
            new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        values = new double[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!TryParseFiniteDouble(parts[index], out values[index]))
            {
                values = Array.Empty<double>();
                return false;
            }
        }

        return true;
    }

    private static string FormatDouble(double value) =>
        value.ToString("G15", CultureInfo.InvariantCulture);
}
