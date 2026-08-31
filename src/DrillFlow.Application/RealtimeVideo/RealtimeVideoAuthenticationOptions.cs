namespace DrillFlow.Application.RealtimeVideo;

public sealed class RealtimeVideoAuthenticationOptions
{
    public RealtimeVideoAuthenticationMode Mode { get; set; } =
        RealtimeVideoAuthenticationMode.None;

    /// <summary>
    /// Logical key for a future DPAPI/Credential Manager-backed token store.
    /// </summary>
    public string CredentialName { get; set; } = string.Empty;

    /// <summary>
    /// Name of an environment variable containing a JWT. This is a name, never the token value.
    /// </summary>
    public string TokenEnvironmentVariable { get; set; } = "DRILLFLOW_SIGNALR_JWT";

    public RealtimeVideoAuthenticationOptions Clone() =>
        (RealtimeVideoAuthenticationOptions)MemberwiseClone();
}
