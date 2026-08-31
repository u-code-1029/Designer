using System;

namespace DrillFlow.Desktop.Services;

public sealed class LiveImageLimitExceededException : Exception
{
    public LiveImageLimitExceededException(string message)
        : base(message)
    {
    }
}
