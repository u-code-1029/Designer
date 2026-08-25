using System;
using System.Threading.Tasks;

namespace DrillFlow.Tests;

internal static class TaskTimeoutExtensions
{
    public static async Task WithTimeoutAsync(this Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException($"The test operation did not complete within {timeout}.");
        }

        await task.ConfigureAwait(false);
    }

    public static async Task<T> WithTimeoutAsync<T>(this Task<T> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException($"The test operation did not complete within {timeout}.");
        }

        return await task.ConfigureAwait(false);
    }
}
