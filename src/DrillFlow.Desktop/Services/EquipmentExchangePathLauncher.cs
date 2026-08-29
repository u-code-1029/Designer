using System;
using System.Diagnostics;
using System.IO;

namespace DrillFlow.Desktop.Services;

public sealed class EquipmentExchangePathLauncher : IEquipmentExchangePathLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    public EquipmentExchangePathLauncher()
        : this(startInfo =>
        {
            Process.Start(startInfo);
        })
    {
    }

    internal EquipmentExchangePathLauncher(Action<ProcessStartInfo> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public string OpenFileLocation(string filePath)
    {
        var normalizedPath = (filePath ?? string.Empty).Trim();
        if (normalizedPath.Length == 0 || !Path.IsPathRooted(normalizedPath))
        {
            throw new ArgumentException(
                "An absolute equipment exchange file path is required.",
                nameof(filePath));
        }

        // Explorer can still open the containing folder after the default lifecycle has removed
        // the transient request/response. Avoid File.Exists because a slow UNC probe would block
        // the WPF dispatcher before Explorer can handle the path itself.
        _start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/select,\"" + normalizedPath.Replace("\"", string.Empty) + "\"",
            UseShellExecute = true
        });

        return normalizedPath;
    }
}
