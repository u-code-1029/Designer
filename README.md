# DrillFlow Designer

DrillFlow Designer is a WPF workflow editor for composing and running drill-equipment actions through a file-based request/response protocol.

## Platform

- Windows 7 SP1 or later
- .NET Framework 4.8
- WPF-UI 4.3 Fluent controls
- Runtime System/Light/Dark themes persisted in user settings
- Generic Host, Microsoft dependency injection and options
- Serilog bootstrap logging with rolling file and debug sinks
- A Windows compatibility manifest (as-invoker, Win7+, DPI-aware)

The application deliberately targets `net48`: modern `.NET 8+` runtimes do not run on Windows 7. Windows 10/11 receive the same functional Fluent UI, while unsupported backdrop effects fall back to a solid background.

The communication-directory browser uses the Windows Shell file-open dialog in folder-selection mode. This keeps the modern Explorer experience and local/UNC support on Windows 7; WPF's `Microsoft.Win32.OpenFolderDialog` is only available on modern .NET, not .NET Framework 4.8. Settings also provides a separate button that opens the currently entered directory in Explorer.

## Solution

- `DrillFlow.Core` — workflow model, expression language, runtime values and validation
- `DrillFlow.Application` — runner and communication/persistence contracts
- `DrillFlow.Infrastructure` — JSON persistence, correlation IDs and file exchange
- `DrillFlow.Desktop` — WPF-UI shell, workflow editor and settings
- `DrillFlow.Tests` — domain, runner, persistence and transport tests

## Build

```powershell
dotnet restore DrillFlow.Designer.sln --configfile NuGet.Config
dotnet build DrillFlow.Designer.sln --no-restore
dotnet test tests\DrillFlow.Tests\DrillFlow.Tests.csproj --no-build
```

## Equipment exchange

The request and response use configurable leaf filenames in one configurable local or UNC directory. The desktop app permits one local instance, and a fixed `.drillflow.exchange.lock` sidecar is held with `FileShare.None` for the complete exchange, so separate app processes and SMB clients cannot publish into that directory concurrently. Operationally, one physical machine/exchange directory must have only one active controller: the sidecar serializes individual exchanges, not operator-session ownership. A positive `index` allocated from persisted high-water blocks is used as the correlation ID; abandoned reservations may create harmless gaps but IDs are never reused. Retained stale responses are ignored until a response with the active index, `command: "return"`, finite absolute `stage_x`/`stage_y` coordinates, and an optional `image_path` is observed.

After a matching response has been captured and validated in memory, the application deletes the completed request file by default, materializes the response values, and then deletes the response file by default. This post-response cleanup is independent of whether the equipment deletes a request immediately after reading it. A missing file is already clean, and access/sharing failures are logged as warnings without discarding the valid response or stopping the next exchange. Operators can independently retain either file for overwrite in Settings.

Automatic timeout retries are disabled by default because resending a physical command can execute it twice. When enabled, a retry republishes the identical payload with the same correlation ID. Reusing the ID enables equipment-side deduplication but does not provide it by itself: the equipment must treat `index` as a durable idempotency key to obtain exactly-once physical execution. Without that equipment contract, retry mode is intentionally at-least-once.

## Workflow values

Parameters retain their authored text, including scientific notation. Text beginning with `=` is evaluated by the sandboxed expression engine, for example:

```text
=measure_1.result.stage_y
=move_1.parameters.move_x + 2.5E-4
```

Arbitrary C# execution is never used. Runtime results exist only for the current session and stay in memory across selection, editing, reordering, save, Undo/Redo, and later selected-Action executions. A new full-workflow Run, New/Open, or an explicit result clear starts the next result session. Repeat iterations remain addressable through `results[index]`, `results.last`, and the latest-result shortcut `result`.

Designer-owned Delay, Repeat, Conditional, and HTTP Actions never use the equipment exchange files. HTTP supports GET/POST and exposes status, headers, raw text, and dynamically parsed JSON through paths such as `http_1.result.json.items[0].id`.

## Live interaction

The Live Interaction page serially publishes `frame` requests with a positive finite metre-based `hfw` and only sends the next request after the matching response image has been loaded fully into memory. The editable 10 mm default can be halved/doubled with the image wheel or `+`/`-`; valid Pixel Pitch calibration follows the same ratio, and movement stays locked until an image captured at the new HFW arrives. It therefore supports equipment that overwrites one shared image file while presenting the result as a continuously refreshed camera view. Normal workflow-result images must instead remain correlation-unique or immutable for the current run. New installations use a 50 ms file-polling interval, adjustable in Settings. The default post-response cleanup removes each completed `frame` request so this high-frequency mode does not leave a request file behind. Stop, page navigation, or HFW replacement cancels the active response wait immediately; the transport then removes only the request bytes owned by that exchange while UI control is released without waiting for the response timeout. On application exit, already-scheduled cleanup is joined only for the remainder of its original two-second deadline so normal local cleanup finishes without making an unavailable UNC share block shutdown indefinitely.

Double-clicking the rendered image, or choosing **Move to this position** from its context menu, maps the pointer from the `Uniform` viewport back to the original pixel coordinates, rejects letterbox regions, measures from the image centre, applies the operator-entered pixel pitch (m/mm/µm/nm) and optional X/Y inversion, and publishes the existing relative `move` command. Before any move or high-quality `capture`, the active frame exchange is canceled and its still-matching request is reclaimed; no new frame is published until the exclusive interaction finishes. A successful image move resumes frames even from a manually stopped preview, while failed, canceled, or navigated-away moves remain stopped for operator review. Capture restores only the prior streaming intent and copies the original image to a user-selected local-drive path through an owned save dialog. The independently scrolling parameter/result column also provides exchange-folder access, one-frame test-response publication, and an optional continuous commissioning simulator that answers each active `frame` request with a new 768×512 mosaic image without overwriting a response that is already present. Live interaction and workflow execution are mutually exclusive for their complete sessions so their shared request/response filenames cannot interleave.

In an Expression editor, `Ctrl+Space` opens context-aware completion for accessible earlier Actions and their `parameters`/`result` members. Actions support Ctrl/Shift multi-selection, `Ctrl+A`/`Esc`, ordered group `Ctrl+C/X/V`, grouped drag/drop, Ctrl-drag deep copy, and mouse-selected insertion slots. Copied workflow batches receive fresh IDs and unique aliases while references between the selected Actions follow their regenerated aliases. The designer keeps its command/status regions fixed while the toolbox, workflow Canvas, and Fluent inspector tabs scroll independently. Action cards show their latest response values, an in-card image preview when `image_path` is usable, and a spinner while running; the inspector includes a dedicated larger image layout. The response simulator creates a random 768×512 PNG below LocalAppData and removes app-owned temporary images on shutdown. Spaced `+` markers expose every valid insertion slot, the lowest-layer execution rail connects only the Start and End markers and is occluded by cards, Canvas zoom is available from 60–160%, and View Reset restores the split layout, scroll positions, inspector tab, and 100% zoom. While paused at a breakpoint, `F10` performs Continue.

See [contract.md](contract.md) for the current equipment request/response contract and format-change map,
[docs/architecture.md](docs/architecture.md) for the agreed behavior and safety boundaries,
[docs/product-and-implementation.md](docs/product-and-implementation.md) for the complete Korean product, event-flow, and implementation guide,
and [docs/deployment.md](docs/deployment.md) for the Windows 7 release checklist.

An importable example is available at [samples/basic-drilling.drillflow.json](samples/basic-drilling.drillflow.json).
