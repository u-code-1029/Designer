using System;

namespace DrillFlow.Infrastructure.Communication;

public enum EquipmentRequestDeletionWaitPhase
{
    BeforeInitialPublish,
    AfterMatchingResponse,
    AfterResponseTimeout,
    BeforeRetry,
}

/// <summary>
/// Raised when delete-after-read equipment does not remove its request file within the configured
/// response timeout. The transport fails closed instead of overwriting a file that may still be
/// deleted later by the equipment.
/// </summary>
public sealed class EquipmentRequestDeletionTimeoutException : TimeoutException
{
    public EquipmentRequestDeletionTimeoutException(
        string requestFilePath,
        EquipmentRequestDeletionWaitPhase phase,
        TimeSpan timeout)
        : base(
            $"Equipment did not delete request file '{requestFilePath}' within {timeout} "
            + $"during {phase}. A new request was not published because a late equipment "
            + "deletion could remove it.")
    {
        RequestFilePath = requestFilePath;
        Phase = phase;
        Timeout = timeout;
    }

    public string RequestFilePath { get; }

    public EquipmentRequestDeletionWaitPhase Phase { get; }

    public TimeSpan Timeout { get; }
}
