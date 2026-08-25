# Windows deployment checklist

## Prerequisites

- Windows 7 SP1 or later.
- .NET Framework 4.8. On Windows 7, verify the .NET Framework Release registry value is at
  least `528040`; it is not part of a clean Windows 7 installation.
- For Windows 7 installation media, apply the current servicing-stack and SHA-2 support updates
  before installing .NET Framework 4.8.

Microsoft maintains the authoritative [.NET Framework system requirements](https://learn.microsoft.com/dotnet/framework/get-started/system-requirements).

## Files to deploy

Deploy the complete published directory. `DrillFlow.Desktop.exe` is not standalone: its
`.exe.config`, `appsettings.json`, WPF-UI, Microsoft.Extensions, Serilog, Newtonsoft.Json, and
compatibility DLLs must remain beside it. PDB files are optional for production diagnostics.

The build is AnyCPU. Sign the production executable and installer with a SHA-256 Authenticode
certificate and timestamp before release.

## Equipment-share checks

- Keep the exchange folder plus request/response filename below the Windows 7 `MAX_PATH` limit.
- For UNC shares, test with the same Windows account that will operate the application.
- Grant create, read, write, rename, and—when a delete lifecycle is selected—delete permission.
- Verify the selected request lifecycle and response lifecycle against the real controller.
- Leave automatic retries disabled unless the controller durably de-duplicates the same `index`.
- Do not reserve `.drillflow.exchange.lock` as the request or response filename.

## Win7 release gate

Before declaring a production build Windows 7-qualified, run it on clean Windows 7 SP1 x86 and
x64 virtual machines and verify:

- startup with Aero enabled and disabled;
- Korean and English resources;
- 100%, 125%, and 150% display scaling;
- nested drag/drop, breakpoint, Step, Continue, and graceful Stop;
- local-folder and real SMB-share exchanges in both file-lifecycle modes;
- response timeout, retry warning, sharing violations, and controller lock contention.

The current development-machine smoke test does not replace this release gate.
