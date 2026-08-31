using System;
using System.Threading;

namespace DrillFlow.Desktop.Services;

public sealed class LiveCaptureSnapshot : IDisposable
{
    private readonly Action<string> _release;
    private string? _path;

    internal LiveCaptureSnapshot(string path, Action<string> release)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public string Path => _path ?? throw new ObjectDisposedException(nameof(LiveCaptureSnapshot));

    public void Dispose()
    {
        var path = Interlocked.Exchange(ref _path, null);
        if (path is not null)
        {
            _release(path);
        }
    }
}
