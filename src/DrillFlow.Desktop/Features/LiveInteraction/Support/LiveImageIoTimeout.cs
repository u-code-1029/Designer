using System;
using System.Threading;

namespace DrillFlow.Desktop.ViewModels;

internal static class LiveImageIoTimeout
{
    private static readonly TimeSpan MinimumBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumCancellationTokenBudget
        = TimeSpan.FromMilliseconds(int.MaxValue);

    public static TimeSpan NormalizeBudget(TimeSpan configured)
    {
        if (configured < MinimumBudget)
        {
            return MinimumBudget;
        }

        return configured > MaximumCancellationTokenBudget
            ? MaximumCancellationTokenBudget
            : configured;
    }

    public static CancellationTokenSource CreateSource(TimeSpan configured)
    {
        return new CancellationTokenSource(NormalizeBudget(configured));
    }

    public static bool IsTimeout(
        CancellationTokenSource timeoutSource,
        CancellationToken lifecycleToken)
    {
        return timeoutSource.IsCancellationRequested
               && !lifecycleToken.IsCancellationRequested;
    }

    public static TimeoutException CreateException(
        TimeSpan configured,
        OperationCanceledException innerException)
    {
        var budget = NormalizeBudget(configured);
        return new TimeoutException(
            $"Image file acquisition exceeded its {budget.TotalMilliseconds:0} ms safety timeout.",
            innerException);
    }
}
