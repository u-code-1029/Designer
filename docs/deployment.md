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
- Configure `EquipmentCommunication.LiveImageDirectory` (Desktop `LiveImageFolder`) as a shared absolute local or UNC folder visible under the same pathname to both the app and equipment. Blank or missing settings fall back to `<ExchangeDirectory>\.drillflow-live`; include the generated `live-<correlation_id>.bmp` suffix when checking `MAX_PATH` and share permissions.
- For UNC shares, test with the same Windows account that will operate the application.
- Grant create, read, write, rename, and—when any request/response cleanup lifecycle is selected—delete permission. The default application policy deletes a completed request after its matching response.
- Verify the equipment request-consumption policy, application post-response request-cleanup policy, and response lifecycle against the real controller.
- Permit exactly one active controller for each physical machine/exchange directory; the per-exchange sidecar lock is not a long-lived operator ownership lock.
- Deploy the build containing Action-specific Stage/Camera/Focus/Integration/Live/Abort XML templates that match the actual equipment schema. App-generated request/test-response wire files are UTF-8 without BOM, while equipment responses accept UTF-8 with or without the standard BOM; all wire payloads are limited to 4 MiB. Strings are XML-escaped, and metre values are emitted as invariant uppercase scientific notation with at least two exponent digits (`1E-06`). Response parsing accepts padded or unpadded exponents and ignores ASCII space/tab/CR/LF differences throughout fixed declaration/tag/indentation text while preserving whitespace inside extracted logical values. Equivalent well-formed pretty-printed and single-line responses therefore both match; malformed XML is rejected. The JSON-like message shown in diagnostics and test dialogs is only an in-memory logical representation. Saving a template with BOM/U+FEFF still fails fast at startup, and an oversized response is ignored until the configured response timeout.
- Keep equipment-returned `image_path` files immutable while the current result session displays them. Live requests `<resolved LiveImageDirectory>\live-<correlation_id>.bmp`; Integration keeps its existing correlation-specific exchange-subdirectory path. The app best-effort deletes an image only when the response returns that exact app-owned requested path, and a cleanup failure must not stop subsequent work.
- Leave automatic retries disabled unless the controller durably de-duplicates the same `correlation_id` and `action`.
- Commission the decimal-second response timeout, polling interval, and pre-publication quiet interval against the real controller. The default quiet interval is 0.1 seconds and may be set to zero; increasing it also lowers Live frame cadence.
- Do not reserve `.drillflow.exchange.lock` as the request or response filename.

## Win7 release gate

Before declaring a production build Windows 7-qualified, run it on clean Windows 7 SP1 x86 and
x64 virtual machines and verify:

- startup with Aero enabled and disabled;
- Korean and English resources;
- 100%, 125%, and 150% display scaling;
- nested drag/drop, breakpoint, Step, Continue, first-click immediate Stop, and stopped-card state;
- Action 1 results remain visible and expression-addressable after a selected Action 2 execution, then clear only on a new full Run, New/Open, or explicit result reset;
- Live right-panel scrolling at small window sizes/high DPI, exchange-folder access, local and UNC Live-image-folder selection/opening, blank-setting fallback, one-frame/continuous test responses, and bounded temporary-image growth during a long continuous test;
- all six Designer equipment Actions publish XML from the correct template and accept only a response whose `type`, `correlation_id`, and `action` match; `result = 1` preserves the failure result and faults the remaining workflow;
- production XML fixtures round-trip every required exact placeholder, preserve ordinary same-name text, escape string paths correctly, accept consistent repeated tokens, and reject missing, unknown, malformed, inconsistently repeated, ambiguous, mismatched-correlation, or mismatched-action data;
- Stage/Camera finite signed coordinates have no artificial ±distance limit; Focus enforces strict HFW/range/step constraints; Integration enforces power-of-two frame counts through 64 and absolute image paths; Live fixes frame count to 1;
- Live Stop and page navigation release the Designer before response timeout and clean up only the still-matching Live request; closing the app with a local active Live exchange drains that owned cleanup within its original bounded deadline and leaves no request behind;
- every `action: "live"` request contains fixed `frame_count: 1`, `<resolved LiveImageDirectory>\live-<correlation_id>.bmp` as its absolute local/UNC `image_path`, and the current metre-based `hfw` within `0 < hfw < 2.4E-3 m`; blank/missing configuration falls back to `<ExchangeDirectory>\.drillflow-live`, while image-wheel and keyboard `+`/`-` changes halve/double HFW within range, replace an active old-HFW request, and persist for later Live requests;
- after an HFW change, the old image remains visible but both double-click and context-menu movement stay disabled until the matching new-HFW Live image is decoded;
- image double-click and **Move to this position** cancel/reclaim the active Live request before publishing a relative Stage Action, keep Live paused through the Stage response, and resume automatically after success even from a manually stopped preview; failure, cancellation, and navigation must not resume;
- high-quality Integration likewise preempts the active Live request, never overlaps request files, and restores only the prior streaming intent;
- local-folder and real SMB-share exchanges in every configured request-consumption, post-response cleanup, and response-lifecycle combination;
- with both application cleanup defaults, a validated matching response causes request deletion first and response deletion after result materialization; denied/share-locked cleanup remains non-fatal and later exchanges still proceed;
- post-response and canceled-request cleanup with an already-missing file and with denied/share-locked deletion; both must preserve mismatched/newer request content and allow later work to continue;
- canceled cleanup with stable-read delay longer than its two-second budget, plus an immediate next exchange whose response timeout is shorter than that cleanup budget;
- response timeout, retry warning, sharing violations, and controller lock contention;
- cancellation during the configured pre-publication delay (no request or temp file may appear), and an immediate next exchange that respects the recovery interval without charging it to response timeout;
- a cancellation-cooperative and cancellation-ignoring HTTP executor; both must reach `Stopped` on the first Stop, and late diagnostics must not contain URL credentials/query/fragment or exception text.

The current development-machine smoke test does not replace this release gate.
