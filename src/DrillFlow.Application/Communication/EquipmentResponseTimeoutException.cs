using System;

namespace DrillFlow.Application.Communication;

public sealed class EquipmentResponseTimeoutException : TimeoutException
{
    public EquipmentResponseTimeoutException(int correlationId, int attempts, TimeSpan timeoutPerAttempt)
        : base(
            $"No matching equipment response was received for correlation ID {correlationId} "
            + $"after {attempts} attempt(s), each with a {timeoutPerAttempt} timeout.")
    {
        CorrelationId = correlationId;
        Attempts = attempts;
        TimeoutPerAttempt = timeoutPerAttempt;
    }

    public int CorrelationId { get; }

    public int Attempts { get; }

    public TimeSpan TimeoutPerAttempt { get; }
}
