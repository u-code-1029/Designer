namespace DrillFlow.Application.RealtimeVideo;

public sealed class RealtimeVideoFrameOptions
{
    public int MaximumFrameBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// A single-slot buffer drops stale frames instead of increasing UI latency.
    /// </summary>
    public int BufferCapacity { get; set; } = 1;

    public RealtimeVideoFrameOptions Clone() => (RealtimeVideoFrameOptions)MemberwiseClone();
}
