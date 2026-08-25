# Architecture and behavior contract

## Workflow nodes

Equipment nodes publish a request and wait for a correlated response:

- Move: `move_mode`, `move_x`, `move_y`
- Measure: `thickness`
- Drill: `thickness`, `drill_result_path`
- Abort: no parameters; ends the workflow after its response

Control-flow nodes execute locally:

- Delay: cancellable delay in milliseconds
- Repeat: evaluates its count once, then runs its nested body
- Conditional: executes the first true If/Else-if branch, otherwise Else

Designer-owned HTTP nodes also bypass the equipment file transport. HTTP supports GET and POST,
evaluated URL/header/body/timeout parameters, and records status, headers, raw body, plus a
dynamically parsed JSON object/array under `result.json`. Non-JSON bodies remain available through
`result.body_text`; non-success HTTP status codes are results, while transport failures and timeouts
fault the run. A normal Stop cancels an in-flight HTTP request because it is a designer operation,
not an already-issued physical equipment action.

Each persisted node has a stable GUID, a unique expression key, a user-facing name, enabled state and optional breakpoint. Runtime results are not part of the workflow document.

## Validation

- Relative and absolute X/Y: `-0.5 m < value < 0.5 m`; negative absolute values represent positions on the opposite side of home `(0, 0)`.
- Thickness: `0 < value <= 2.4E-3 m`.
- Repeat count: `1..Int32.MaxValue`.
- Delay: `0..29999 ms`.
- Numeric values must be finite.
- Expression references use stable keys and may not introduce cycles or depend on a future action that cannot have executed.

## Runner state

The runner moves through `Idle`, `Running`, `Paused`, `Stopping`, and a terminal `Completed`, `Stopped`, or `Faulted` state. A breakpoint pauses immediately before its node. Continue resumes until the next breakpoint; Step executes one node and pauses again.

Toolbar Stop never sends an equipment abort. It prevents the next node from starting. If an equipment request is already in flight, the runner remains `Stopping`, receives and records that response, and then becomes `Stopped`. The explicit Abort workflow node still publishes `command: "abort"` and terminates the sequence.

Pressing Stop again while the runner is already `Stopping` force-cancels the local wait immediately. This second press still never publishes `abort`; a request that was already published remains owned by the equipment, while temporary app files and the exchange lock are released. The terminal state cannot regress back to `Stopping` if natural completion wins the race.

## File lifecycle

The exchange directory may be local or UNC. Request and response filenames are separate, configurable leaf filenames including their extensions.

Request modes:

- Equipment deletes the request after reading it.
- Equipment retains the request and the next publication replaces it.

Response modes:

- Application deletes the response after reading it (default).
- Application retains the response for the equipment to overwrite.

Publishing uses a complete temporary file followed by a same-directory replace/move. The transport holds the fixed `.drillflow.exchange.lock` sidecar open with `FileShare.None` for the entire request/response exchange. Windows and SMB therefore serialize separate app processes or workstations that point at the same directory; lock acquisition is cancellable and bounded by the configured response timeout, and it never publishes a request on timeout. Response observation uses stable-file polling and retries transient sharing violations. Only a parseable response whose `index` matches the in-flight request and whose `command` equals `return` is accepted. Additional top-level response fields are retained dynamically.

Timeout retry is disabled by default. If enabled, the app republishes byte-identical JSON with the same correlation `index`. This is an at-least-once protocol unless the equipment durably remembers processed indices and returns the prior result without repeating the physical command. Correlation matching alone is not an equipment-side idempotency guarantee.

For commissioning, the designer can create an editable test response for the selected equipment Action in a WPF-UI ContentDialog. It uses a detected request's correlation index, atomically publishes the response, and in equipment-delete mode removes only a request with the same index. This simulator does not acquire the exchange lock because the real transport intentionally holds it while waiting for that response.

## Persistence and diagnostics

Workflow documents use a schema-versioned `.drillflow.json` representation and support nested nodes. Current-run results and exchange payloads remain in memory only. Serilog rolling files are diagnostic logs, not a resumable run archive. After a crash, the operator reopens the last explicitly saved design; an uncertain physical operation is never resumed automatically.
