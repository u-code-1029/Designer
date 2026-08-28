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
- `DrillFlow.Infrastructure` — JSON workflow persistence, fixed-template XML codec, correlation IDs and file exchange
- `DrillFlow.Desktop` — WPF-UI shell, workflow editor and settings
- `DrillFlow.Tests` — domain, runner, persistence and transport tests

## Build

```powershell
dotnet restore DrillFlow.Designer.sln --configfile NuGet.Config
dotnet build DrillFlow.Designer.sln --no-restore
dotnet test tests\DrillFlow.Tests\DrillFlow.Tests.csproj --no-build
```

## Equipment exchange

The request and response use configurable leaf filenames—`request.xml` and `response.xml` by default—in one configurable local or UNC directory. The in-memory logical object has `type`, `correlation_id`, `action`, and action-specific fields; no intermediate JSON file is written. The nine Designer equipment Actions are Stage, Camera, Focus, Integration, Live, Abort, OM, Lens, and Auto Contrast/Brightness (`acb`). On the wire, one of 25 embedded dummy XML answer-sheet templates is rendered or parsed as plain text: every Action has request/success-response templates, and the seven Actions with success-only result fields also have a common-envelope `failure-response.xml`. Only exact `{{{field}}}` tokens are replaced or extracted, while ordinary occurrences of the field name remain untouched. A token may be repeated when the same value belongs in multiple positions; all extracted occurrences must agree. Response matching ignores spaces, tabs, CR, and LF throughout the fixed template text—including declarations, tags, and indentation—so an indented template accepts the equipment's equivalent single-line XML. Matching uses a whitespace-free comparison view but extracts values from the original well-formed XML; consequently outer formatting is ignored while meaningful spaces inside an `image_path` or Focus matrix are preserved. Metre values are written with invariant uppercase scientific notation and at least two exponent digits (`1E-06`); input accepts both `1E-06` and `1E-6`. The templates are grouped by Action under `src/DrillFlow.Infrastructure/Communication/Templates` so the final equipment XML can be substituted in one obvious location. Templates and app-generated wire payloads are UTF-8 without BOM; equipment responses accept the standard UTF-8 BOM and remove it in memory before parsing. All wire payloads are capped at 4 MiB, and oversized responses are rejected before payload allocation.

Timeout and polling settings are entered as decimal seconds. Each new logical equipment exchange also has a configurable, cancellation-aware quiet interval before its first request publication (0.1 seconds by default). The interval is held while the exchange owns the local/SMB serialization lock, does not consume response-timeout budget, and leaves no request file when canceled. Retries continue to use their separate retry interval.

The desktop app permits one local instance, and a fixed `.drillflow.exchange.lock` sidecar is held with `FileShare.None` for the complete exchange, so separate app processes and SMB clients cannot publish into that directory concurrently. Operationally, one physical machine/exchange directory must have only one active controller: the sidecar serializes individual exchanges, not operator-session ownership. A positive `correlation_id` allocated from persisted high-water blocks is never reused; gaps are harmless. A success response requires the active correlation, matching action, valid action-specific fields, and `result: 0`. A `result: 1` failure may contain only the common `type`, `correlation_id`, `action`, and `result` fields; it is preserved for inspection, faults that Action, and stops the Workflow.

After a matching response has been captured and validated in memory, the application deletes the completed request file by default, materializes the response values, and then deletes the response file by default. This post-response cleanup is independent of whether the equipment deletes a request immediately after reading it. A missing file is already clean, and access/sharing failures are logged as warnings without discarding the valid response or stopping the next exchange. Operators can independently retain either file for overwrite in Settings.

Automatic timeout retries are disabled by default because resending a physical command can execute it twice. When enabled, a retry republishes the identical XML payload with the same correlation ID. Reusing the ID enables equipment-side deduplication but does not provide it by itself: the equipment must treat `correlation_id` as a durable idempotency key to obtain exactly-once physical execution. Without that equipment contract, retry mode is intentionally at-least-once.

## Workflow values

Parameters retain their authored text, including scientific notation. Text beginning with `=` is evaluated by the sandboxed expression engine, for example:

```text
=stage_1.result.current_stage_y
=camera_1.parameters.camera_x + 2.5E-4
```

Arbitrary C# execution is never used. Runtime results exist only for the current session and stay in memory across selection, editing, reordering, save, Undo/Redo, and later selected-Action executions. A new full-workflow Run, New/Open, or an explicit result clear starts the next result session. Repeat iterations remain addressable through `results[index]`, `results.last`, and the latest-result shortcut `result`.

Designer-owned Delay, Repeat, Conditional, and HTTP Actions never use the equipment exchange files. HTTP supports GET/POST and exposes status, headers, raw text, and dynamically parsed JSON through paths such as `http_1.result.json.items[0].id`.

## Live interaction

The Live Interaction page serially publishes `live` requests with `frame_count: 1`, an absolute app-proposed `image_path`, and a metre-based `hfw` satisfying `0 < hfw < 2.4 mm`. Settings exposes a local/UNC Live image folder (`EquipmentCommunication.LiveImageDirectory`, persisted as Desktop `LiveImageFolder`). A blank or missing value falls back to `<ExchangeDirectory>\.drillflow-live`; each wire request uses `<resolved folder>\live-<correlation_id>.bmp`, so canceled or late frames cannot overwrite another correlation's image. This setting affects only `live`; Integration retains its existing correlation-specific exchange-subdirectory path. The 1 mm HFW default can be halved/doubled with the image wheel or `+`/`-`; valid Pixel Pitch calibration follows the same ratio, and movement stays locked until an image captured at the new HFW arrives. The next request is sent only after the matching image has been decoded fully into memory. An app-owned frame is deleted best-effort after use when the equipment returns that exact requested path; cleanup failures do not stop streaming, and a different response path is treated as equipment-owned and preserved. Stop, navigation, or HFW replacement cancels the active response wait immediately and reclaims only the request bytes owned by that exchange.

Double-clicking the rendered image, or choosing **Move to this position**, maps the pointer from the `Uniform` viewport back to the original pixel coordinates, rejects letterbox regions, applies the operator-entered pixel pitch (m/mm/µm/nm) and optional X/Y inversion, and publishes a relative `stage` request. The calculated X/Y values need only be finite signed numbers. Before Stage, Camera, Focus, Integration, Lens, or ACB begins, the active Live exchange is canceled and its still-matching request reclaimed; no new frame is published until that exclusive interaction finishes. Lens accepts `lens1`, `lens2`, or `no_change` and returns the actual `lens1`/`lens2` mode; ACB uses the current valid HFW. Integration captures 1/2/4/8/16/32/64 frames and copies a secured snapshot to a user-selected local-drive path. Every item in the independently scrolling right column—including state, latest target, Stage, Camera, HFW, pixel calibration, Focus, Lens, ACB, and capture results—is an initially collapsed expandable card; the page also provides exchange-folder access, one-frame test publication, and a continuous commissioning simulator. OM and Abort are deliberately excluded from Live Interaction. Live interaction and Workflow execution remain mutually exclusive so their shared filenames cannot interleave.

In an Expression editor, `Ctrl+Space` opens context-aware completion for accessible earlier Actions and their `parameters`/`result` members. Actions support Ctrl/Shift multi-selection, `Ctrl+A`/`Esc`, ordered group `Ctrl+C/X/V`, grouped drag/drop, Ctrl-drag deep copy, and mouse-selected insertion slots. Copied workflow batches receive fresh IDs and unique aliases while references between the selected Actions follow their regenerated aliases. The designer keeps its command/status regions fixed while the toolbox, workflow Canvas, and Fluent inspector tabs scroll independently. Action cards show their latest response values, an in-card image preview when `image_path` is usable, and a spinner while running; the inspector includes a dedicated larger image layout. The response simulator creates a random 768×512 PNG below LocalAppData and removes app-owned temporary images on shutdown. Spaced `+` markers expose every valid insertion slot, the lowest-layer execution rail connects only the Start and End markers and is occluded by cards, Canvas zoom is available from 60–160%, and View Reset restores the split layout, scroll positions, inspector tab, and 100% zoom. While paused at a breakpoint, `F10` performs Continue.

See [contract.md](contract.md) for the current equipment request/response contract and format-change map,
[docs/architecture.md](docs/architecture.md) for the agreed behavior and safety boundaries,
[docs/product-and-implementation.md](docs/product-and-implementation.md) for the complete Korean product, event-flow, and implementation guide,
and [docs/deployment.md](docs/deployment.md) for the Windows 7 release checklist.

An importable example is available at [samples/basic-drilling.drillflow.json](samples/basic-drilling.drillflow.json).
