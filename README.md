# DrillFlow Designer

DrillFlow Designer is a WPF workflow editor for composing and running drill-equipment actions through a file-based request/response protocol.

## Platform

- Windows 7 SP1 or later
- .NET Framework 4.8
- WPF-UI 4.3 Fluent controls
- Generic Host, Microsoft dependency injection and options
- Serilog bootstrap logging with rolling file and debug sinks
- A Windows compatibility manifest (as-invoker, Win7+, DPI-aware)

The application deliberately targets `net48`: modern `.NET 8+` runtimes do not run on Windows 7. Windows 10/11 receive the same functional Fluent UI, while unsupported backdrop effects fall back to a solid background.

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

The request and response use configurable leaf filenames in one configurable local or UNC directory. The desktop app permits one local instance, and a fixed `.drillflow.exchange.lock` sidecar is held with `FileShare.None` for the complete exchange, so separate app processes and SMB clients cannot publish into that directory concurrently. A persisted, monotonically increasing positive `index` is used as the correlation ID. Retained stale responses are ignored until a response with the active index and `command: "return"` is observed.

Automatic timeout retries are disabled by default because resending a physical command can execute it twice. When enabled, a retry republishes the identical payload with the same correlation ID. Reusing the ID enables equipment-side deduplication but does not provide it by itself: the equipment must treat `index` as a durable idempotency key to obtain exactly-once physical execution. Without that equipment contract, retry mode is intentionally at-least-once.

## Workflow values

Parameters retain their authored text, including scientific notation. Text beginning with `=` is evaluated by the sandboxed expression engine, for example:

```text
=measure_1.result.measured_distance
=move_1.parameters.move_x + 2.5E-4
```

Arbitrary C# execution is never used. Runtime results exist only for the current run and are cleared before the next run. Repeat iterations remain addressable through `results[index]`, `results.last`, and the latest-result shortcut `result`.

Designer-owned Delay, Repeat, Conditional, and HTTP Actions never use the equipment exchange files. HTTP supports GET/POST and exposes status, headers, raw text, and dynamically parsed JSON through paths such as `http_1.result.json.items[0].id`.

In an Expression editor, `Ctrl+Space` opens context-aware completion for accessible earlier Actions and their `parameters`/`result` members. Actions support a grouped right-click menu, `Ctrl+C/X/V`, Ctrl-drag deep copy, and mouse-selected insertion bars; copied nested workflows receive fresh IDs and unique aliases.

See [contract.md](contract.md) for the current equipment request/response contract and format-change map,
[docs/architecture.md](docs/architecture.md) for the agreed behavior and safety boundaries,
[docs/product-and-implementation.md](docs/product-and-implementation.md) for the complete Korean product, event-flow, and implementation guide,
and [docs/deployment.md](docs/deployment.md) for the Windows 7 release checklist.

An importable example is available at [samples/basic-drilling.drillflow.json](samples/basic-drilling.drillflow.json).
