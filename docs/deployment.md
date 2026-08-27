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
- Grant create, read, write, rename, and—when any request/response cleanup lifecycle is selected—delete permission. The default application policy deletes a completed request after its matching response.
- Verify the equipment request-consumption policy, application post-response request-cleanup policy, and response lifecycle against the real controller.
- Permit exactly one active controller for each physical machine/exchange directory; the per-exchange sidecar lock is not a long-lived operator ownership lock.
- Keep ordinary workflow `image_path` files correlation-unique or immutable until the current Run ends. Reusing one path is supported only by the sequential Live frame loop.
- Leave automatic retries disabled unless the controller durably de-duplicates the same `index`.
- Do not reserve `.drillflow.exchange.lock` as the request or response filename.

## Win7 release gate

Before declaring a production build Windows 7-qualified, run it on clean Windows 7 SP1 x86 and
x64 virtual machines and verify:

- startup with Aero enabled and disabled;
- Korean and English resources;
- 100%, 125%, and 150% display scaling;
- nested drag/drop, breakpoint, Step, Continue, first-click immediate Stop, and stopped-card state;
- Action 1 results remain visible and expression-addressable after a selected Action 2 execution, then clear only on a new full Run, New/Open, or explicit result reset;
- Live right-panel scrolling at small window sizes/high DPI, exchange-folder access, one-frame/continuous test responses, and bounded temporary-image growth during a long continuous test;
- Live Stop and page navigation release the Designer before response timeout and clean up only the still-matching frame request; closing the app with a local active frame drains that owned cleanup within its original bounded deadline and leaves no request behind;
- every `frame` request contains the current positive finite metre-based `hfw`; image-wheel and keyboard `+`/`-` changes halve/double it, replace an active old-HFW request, and persist for later frames;
- after an HFW change, the old image remains visible but both double-click and context-menu movement stay disabled until the matching new-HFW frame is decoded;
- image double-click and **Move to this position** cancel/reclaim the active frame before publishing `move`, keep frames paused through the move response, and resume automatically after success even from a manually stopped preview; failure, cancellation, and navigation must not resume;
- high-quality capture likewise preempts the active frame, never overlaps request files, and restores only the prior streaming intent;
- local-folder and real SMB-share exchanges in every configured request-consumption, post-response cleanup, and response-lifecycle combination;
- with both application cleanup defaults, a validated matching response causes request deletion first and response deletion after result materialization; denied/share-locked cleanup remains non-fatal and later exchanges still proceed;
- post-response and canceled-request cleanup with an already-missing file and with denied/share-locked deletion; both must preserve mismatched/newer request content and allow later work to continue;
- canceled cleanup with stable-read delay longer than its two-second budget, plus an immediate next exchange whose response timeout is shorter than that cleanup budget;
- response timeout, retry warning, sharing violations, and controller lock contention;
- a cancellation-cooperative and cancellation-ignoring HTTP executor; both must reach `Stopped` on the first Stop, and late diagnostics must not contain URL credentials/query/fragment or exception text.

The current development-machine smoke test does not replace this release gate.
