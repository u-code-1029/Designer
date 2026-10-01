using System;
using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Infrastructure.Communication.FileExchange;

/// <summary>
/// Includes cancellable file-stability waits in a phase's remaining timeout, while keeping
/// operator cancellation distinct from an unavailable sample at the timeout boundary.
/// </summary>
internal static class EquipmentFileReadBudget
{
    public static async Task<byte[]?> TryReadAsync(
        IStableEquipmentFileReader reader,
        string path,
        TimeSpan stableReadDelay,
        int maximumPayloadBytes,
        TimeSpan remainingBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (remainingBudget <= TimeSpan.Zero)
        {
            return null;
        }

        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(remainingBudget);
            try
            {
                var payload = await reader.TryReadAsync(
                        path,
                        stableReadDelay,
                        maximumPayloadBytes,
                        budget.Token)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return budget.IsCancellationRequested ? null : payload;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
