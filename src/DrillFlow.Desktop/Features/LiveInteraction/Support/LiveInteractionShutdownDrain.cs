using System;
using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Desktop.ViewModels;

internal static class LiveInteractionShutdownDrain
{
    public static async Task<bool> WaitForCompletionAsync(Task operation, TimeSpan timeout)
    {
        if (operation is null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (operation.IsCompleted)
        {
            return true;
        }

        using (var timeoutCancellation = new CancellationTokenSource())
        {
            var timeoutTask = Task.Delay(timeout, timeoutCancellation.Token);
            var winner = await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false);
            if (ReferenceEquals(winner, operation))
            {
                timeoutCancellation.Cancel();
                return true;
            }

            return false;
        }
    }
}
