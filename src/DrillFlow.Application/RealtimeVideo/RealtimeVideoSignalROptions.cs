namespace DrillFlow.Application.RealtimeVideo;

public sealed class RealtimeVideoSignalROptions
{
    public string HubEndpoint { get; set; } = string.Empty;

    public string StreamMethod { get; set; } = "StreamFrames";

    /// <summary>
    /// Long polling is the compatible default for the supported Windows 7 baseline. Operators
    /// can select automatic negotiation or another transport after commissioning the endpoint.
    /// </summary>
    public RealtimeVideoTransport Transport { get; set; } = RealtimeVideoTransport.LongPolling;

    public double ServerTimeoutSeconds { get; set; } = 30d;

    public double KeepAliveIntervalSeconds { get; set; } = 15d;

    public RealtimeVideoSignalROptions Clone() => (RealtimeVideoSignalROptions)MemberwiseClone();
}
