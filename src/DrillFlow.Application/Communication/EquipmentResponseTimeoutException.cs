using System;

namespace DrillFlow.Application.Communication;

public sealed class EquipmentResponseTimeoutException : TimeoutException
{
    public EquipmentResponseTimeoutException(int correlationIndex, int attempts, TimeSpan timeoutPerAttempt)
        : base(
            $"No matching equipment response was received for correlation index {correlationIndex} "
            + $"after {attempts} attempt(s), each with a {timeoutPerAttempt} timeout.")
    {
        CorrelationIndex = correlationIndex;
        Attempts = attempts;
        TimeoutPerAttempt = timeoutPerAttempt;
    }

    public int CorrelationIndex { get; }

    public int Attempts { get; }

    public TimeSpan TimeoutPerAttempt { get; }
}

