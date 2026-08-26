using System;
using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Desktop.Services;

public interface IContentDialogGate
{
    Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default);
}

public sealed class ContentDialogGate : IContentDialogGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
