using System;
using System.Diagnostics;
using DrillFlow.Application.Communication;
using Microsoft.Extensions.Options;

namespace DrillFlow.Desktop.Services;

public sealed class ExchangeFolderLauncher : IExchangeFolderLauncher
{
    private readonly EquipmentCommunicationOptions _options;

    public ExchangeFolderLauncher(IOptions<EquipmentCommunicationOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public string Open()
    {
        var directory = (_options.ExchangeDirectory ?? string.Empty).Trim();
        if (directory.Length == 0)
        {
            throw new InvalidOperationException("The equipment exchange directory is not configured.");
        }

        // Let Explorer resolve the path itself. Probing Directory.Exists here can block the WPF
        // dispatcher for a long time when a configured UNC host is temporarily unavailable.
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/e,\"" + directory.Replace("\"", string.Empty) + "\"",
            UseShellExecute = true
        });

        return directory;
    }
}
