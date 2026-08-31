using System;

namespace DrillFlow.Application.RealtimeVideo;

public sealed class RealtimeVideoRetryOptions
{
    public bool Enabled { get; set; } = true;

    public int InitialConnectMaximumAttempts { get; set; } = 5;

    public double[] ReconnectDelaysSeconds { get; set; } = { 0d, 2d, 10d, 30d };

    public RealtimeVideoRetryOptions Clone() => new()
    {
        Enabled = Enabled,
        InitialConnectMaximumAttempts = InitialConnectMaximumAttempts,
        ReconnectDelaysSeconds = (double[])(ReconnectDelaysSeconds?.Clone() ?? Array.Empty<double>())
    };
}
