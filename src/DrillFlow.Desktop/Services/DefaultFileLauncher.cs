using System;
using System.Diagnostics;
using System.IO;

namespace DrillFlow.Desktop.Services;

public sealed class DefaultFileLauncher : IDefaultFileLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    public DefaultFileLauncher()
        : this(startInfo =>
        {
            using (Process.Start(startInfo))
            {
            }
        })
    {
    }

    internal DefaultFileLauncher(Action<ProcessStartInfo> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public string Open(string filePath)
    {
        var normalizedPath = (filePath ?? string.Empty).Trim();
        if (normalizedPath.Length == 0)
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }

        if (!Path.IsPathRooted(normalizedPath))
        {
            throw new ArgumentException("The file path must be absolute.", nameof(filePath));
        }

        // Do not probe File.Exists here. A shell-associated file can live on a temporarily slow
        // mapped/UNC path, and such a probe would synchronously stall the WPF dispatcher before
        // Windows has a chance to report the launch result itself.
        var startInfo = new ProcessStartInfo
        {
            FileName = normalizedPath,
            UseShellExecute = true,
        };
        _start(startInfo);
        return normalizedPath;
    }
}
