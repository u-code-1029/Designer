namespace DrillFlow.Application.RealtimeVideo;

/// <summary>
/// Describes the future real-time equipment video connection. The access token itself is
/// deliberately not part of this document; only a protected credential name or environment
/// variable name may be persisted.
/// </summary>
public sealed class RealtimeVideoOptions
{
    public const string SectionName = "DrillFlow:RealtimeVideo";

    public bool Enabled { get; set; }

    public RealtimeVideoSignalROptions SignalR { get; set; } = new();

    public RealtimeVideoAuthenticationOptions Authentication { get; set; } = new();

    public RealtimeVideoRetryOptions Retry { get; set; } = new();

    public RealtimeVideoFrameOptions Frames { get; set; } = new();

    public RealtimeVideoOptions Clone() => new()
    {
        Enabled = Enabled,
        SignalR = (SignalR ?? new RealtimeVideoSignalROptions()).Clone(),
        Authentication = (Authentication ?? new RealtimeVideoAuthenticationOptions()).Clone(),
        Retry = (Retry ?? new RealtimeVideoRetryOptions()).Clone(),
        Frames = (Frames ?? new RealtimeVideoFrameOptions()).Clone()
    };
}
