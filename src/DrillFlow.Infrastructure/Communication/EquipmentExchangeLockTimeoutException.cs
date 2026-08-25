using System;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// Raised when another process or workstation keeps the exchange-directory sidecar locked beyond
/// the configured response timeout. No request has been published when this exception is raised.
/// </summary>
public sealed class EquipmentExchangeLockTimeoutException : TimeoutException
{
    public EquipmentExchangeLockTimeoutException(
        string lockFilePath,
        TimeSpan timeout,
        Exception? innerException = null)
        : base(
            $"Could not acquire the equipment exchange lock '{lockFilePath}' within {timeout}. "
            + "Another DrillFlow controller may still be using this exchange directory. "
            + "No request was published.",
            innerException)
    {
        LockFilePath = lockFilePath;
        Timeout = timeout;
    }

    public string LockFilePath { get; }

    public TimeSpan Timeout { get; }
}
