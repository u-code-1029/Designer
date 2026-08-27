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
fault the run. Stop cancels an in-flight HTTP request because it is a designer operation. The runner
also races the executor task with its stop token, so a custom executor or .NET Framework body read
that ignores cancellation cannot keep the workflow non-terminal; its late completion is observed in
the background and logged without credentials, query, fragment, headers, body, or exception text.

Each persisted node has a stable GUID, a unique expression key, a user-facing name, enabled state and optional breakpoint. Runtime results are not part of the workflow document. They remain attached to the in-memory Action identity through ordinary editing, reordering, save, Undo/Redo and selected-Action executions. A selected-Action execution runs only that subtree but retains the complete workflow as its expression context and appends to the current result session. A new full run, New/Open, explicit result clear, or process exit ends that result session.

Every equipment response contains the correlated `index`, `command: "return"`, and finite numeric `stage_x`/`stage_y` values representing the stage's absolute position in metres from home `(0, 0)`. `image_path` is optional because not every action captures an image. Unknown additional response fields remain available dynamically. The designer shows the latest response in a default-expanded section under each Action card, loads a valid centered image without retaining a file handle, reports missing/failed image loads explicitly, and offers a larger image tab plus a Windows shell-open command for the selected Action. Command-bar actions collapse every result without releasing it or explicitly clear every result/image; bare `+`/`-` changes the selected result image from 50% to 300% in 25% steps without stealing text-editor input.

## Validation

- Relative and absolute X/Y: `-0.5 m < value < 0.5 m`; negative absolute values represent positions on the opposite side of home `(0, 0)`.
- Thickness: `0 < value <= 2.4E-3 m`.
- Repeat count: `1..Int32.MaxValue`.
- Delay: `0..29999 ms`.
- Numeric values must be finite.
- Expression references use stable keys and may not introduce cycles or depend on a future action that cannot have executed.

## Runner state

The runner moves through `Idle`, `Running`, `Paused`, `Stopping`, and a terminal `Completed`, `Stopped`, or `Faulted` state. The Canvas Start and End terminals invoke the same Run and local Stop commands as the command bar. A breakpoint pauses immediately before its node. Continue resumes until the next breakpoint; Step executes one node and pauses again. A card-level indeterminate progress indicator and response-waiting status are visible while its node is `Running`, including the interval after a request is published and before the matching response arrives. In normal execution the awaited transport task is the completion boundary: without a matching response the runner cannot advance or complete except through configured timeout exhaustion. Operator Stop is the explicit cancellation boundary.

The first Toolbar/Canvas/Action-context Stop immediately cancels the current local operation and response wait, marks a breakpoint-paused or running card `Stopped`, and prevents every later node from starting. It never sends an equipment abort; the explicit Abort workflow node remains the only path that publishes `command: "abort"`. If a valid response wins the cancellation race it completes normally, otherwise the canceled exchange is observed in the background so the UI and runner can become terminal without waiting for filesystem or SMB latency. A repeated Stop is an idempotent compatibility action rather than a second force level, and a terminal state cannot regress to `Stopping` if natural completion wins.

## File lifecycle

The exchange directory may be local or UNC. Request and response filenames are separate, configurable leaf filenames including their extensions.

Equipment request-consumption modes:

- Equipment deletes the request after reading it.
- Equipment retains the request and the next publication replaces it.

Application post-response request-cleanup modes:

- Application best-effort deletes the completed request after accepting its matching response (default).
- Application retains the completed request for the next publication to replace.

Response modes:

- Application deletes the response after reading it (default).
- Application retains the response for the equipment to overwrite.

Publishing uses a complete temporary file followed by a same-directory replace/move. The transport holds the fixed `.drillflow.exchange.lock` sidecar open with `FileShare.None` for the entire request/response exchange. Windows and SMB therefore serialize separate app processes or workstations that point at the same directory; lock acquisition is cancellable and bounded by the configured response timeout, and it never publishes a request on timeout. Response observation uses stable-file polling and retries transient sharing violations. Only a parseable response whose `index` matches the in-flight request and whose `command` equals `return` is accepted. Additional top-level response fields are retained dynamically.

The equipment-consumption and application-cleanup request policies are independent. With the default application cleanup, a request that still exists after its correlated response is accepted is deleted before the exchange releases ownership. Missing files count as successful cleanup. Unauthorized access, sharing violations and other cleanup failures emit a warning but never invalidate the accepted response or fault the caller; retained leftovers can be replaced atomically by a later publication. This best-effort boundary is particularly important for the high-frequency Live frame loop.

Operator cancellation has a separate ownership-safe cleanup path regardless of the configured post-response retention policy. If publication completed, the canceled exchange transfers both its still-held sidecar lock and same-instance in-process gate to a bounded background cleanup. It deletes only a stable request whose bytes exactly equal that exchange's serialized payload, treats an already-missing file as complete, preserves mismatched content, and retries transient read/delete failures for up to two seconds—including stable-read delay—before logging a warning. A following run on the same transport waits behind that gate instead of spending its short response timeout contending with its own cleanup; other processes remain serialized by the sidecar. Normal Stop/HFW replacement returns UI control before that task completes, while transport disposal joins already-scheduled cleanup only for the remainder of its original deadline so a normal process exit does not strand the last owned request. Deleting an unread request can prevent pickup; it cannot retract a physical command the equipment already consumed. A synchronous UNC/SMB kernel call cannot be force-interrupted on .NET Framework 4.8; it remains on a background worker and neither the UI nor application exit waits beyond the original cleanup deadline.

Correlation allocation persists an atomic high-water mark instead of synchronously rewriting the state for every request. Each provider reserves up to 256 positive Int32 IDs while holding the state sidecar process lock, then serves that block from memory. A restart deliberately abandons any unused suffix, preserving uniqueness and preventing stale-response reuse at the cost of harmless gaps; independent providers may therefore expose interleaved, non-contiguous values. The last partial block ends at `Int32.MaxValue`, after which allocation fails rather than wrapping.

Timeout retry is disabled by default. If enabled, the app republishes byte-identical JSON with the same correlation `index`. This is an at-least-once protocol unless the equipment durably remembers processed indices and returns the prior result without repeating the physical command. Correlation matching alone is not an equipment-side idempotency guarantee.

For commissioning, the designer can create an editable test response for the selected equipment Action in a WPF-UI ContentDialog. It uses a detected request's correlation index, supplies `stage_x`/`stage_y`, creates a 768×512 mosaic PNG in an app-owned LocalAppData temporary-image directory, and retains a frozen in-memory bitmap for an immediate preview. Regeneration preserves edited JSON fields and replaces only the preview plus `image_path`; publication is disabled while replacement is in progress. It atomically publishes the response and, in equipment-delete mode, removes only a request with the same index. This simulator does not acquire the exchange lock because the real transport intentionally holds it while waiting for that response. The host-owned temporary image service releases every file handle and removes its generated files on normal shutdown; it also cleans orphans from its dedicated directory at the next startup.

## Live interaction

The application service `ILiveInteractionSession` builds three operator-driven commands on the same transport and correlation provider as the workflow runner: low-latency `frame` with required positive finite metre-based `hfw`, existing `move` with `move_mode: "relative"`, and parameterless high-quality `capture`. The session serializes its own calls, verifies the returned index, validates HFW before publication, and requires `image_path` for frame/capture. The transport's process/SMB exchange lock remains the final I/O serialization boundary.

The deployment contract permits one active controller per physical machine/exchange directory. The sidecar lock prevents file-level overlap between individual exchanges but is intentionally not a distributed operator-session lease. Under that ownership rule the Live loop may reuse one image pathname because it finishes the stable read before publishing another request; ordinary workflow response images must be correlation-unique or remain immutable through the current Run.

The Desktop frame loop is deliberately sequential: response reception, bounded stable-read plus WIC-decode retries, frozen preview publication, then the next request. File/network reads run on workers and both WIC passes run through a singleton queue on a dedicated background STA thread, never the UI Dispatcher. The metadata pass uses delayed creation with non-eager caching, validates dimensions, and only then performs the preview decode. Canceled queued items are skipped and the frozen result is safe to publish on the UI thread. The same service and stable-reader path also render workflow Action-card and Inspector result images; display failure remains non-fatal to workflow execution. The original dimensions and X/Y DPI are retained while preview decoding is capped at 1920 pixels. Encoded input is read into one exact-sized byte array and limited to 64 MiB, either source axis to 16,384 pixels, and total source pixels to 64 million; violations surface an explicit operator-visible error without repeated decode attempts. A 33 ms minimum cadence prevents a zero-latency mock controller from spinning without bound; transient failures retry with bounded exponential backoff while preserving the last good image.

The editable HFW starts at 10 mm and has no manufactured device upper/lower bound beyond being positive and finite. Mouse-wheel over the image or `+`/`-` outside editing controls halves/doubles it. A valid operator Pixel Pitch is scaled by the same new/old HFW ratio. A valid HFW edit cancels an already-published old-HFW frame and restarts the loop with the latest value; text changes are debounced for 300 ms, while wheel/key/button gestures apply immediately. The previous image remains visible but is calibration-pending and cannot initiate a move until a frame requested with the current HFW has been decoded.

New installations use a 50 ms transport polling interval to keep the file-backed preview responsive; operators may raise it in Settings when an SMB appliance needs lower polling pressure. Stable-file checks and the one-in-flight-request rule remain unchanged.

After a matching frame response, image read/decode gets a separate timeout budget derived from the current equipment `ResponseTimeout` (with a one-second safety minimum) and linked to stream/navigation/shutdown cancellation. A timeout is reported as an image failure and enters normal frame backoff; Stop/navigation remains a quiet cancellation. Capture applies the same timer only while securing and validating the owned snapshot. The timer is disposed before SaveFileDialog opens, so operator decision time and the subsequent local copy are not counted against equipment image-I/O time.

Pointer mapping uses the original pixel dimensions and the bitmap's X/Y DPI metadata, matching WPF's device-independent natural size and `Stretch=Uniform` letterboxing even for anisotropic DPI. Image centre is zero displacement; right/down are +X/+Y by default, with independent installation-axis inversion. Pixel pitch is stored as metres after validating a positive finite m/mm/µm/nm input. The existing strict `-0.5 m < value < 0.5 m` move guard is applied before publication.

Move and capture stop scheduling frames, immediately cancel and reclaim the current frame exchange, and perform one exclusive command only after that frame loop has drained. A successful image-target move always resumes framing while the page remains active, including when initiated from a manually stopped preview; move failure/cancellation/navigation deliberately stays stopped for operator review. Capture preserves the previous streaming intent. The image double-click and its “move here” context-menu command share the same pixel/DPI/letterbox mapper. Workflow execution, response simulation and communication-setting edits are disabled for the whole live activity, including gaps between frames; live commands are similarly disabled while the runner is Validating/Running/Paused/Stopping. Live Stop cancels the current frame wait, while page navigation and shutdown also cancel an app-owned move/capture, without waiting for `ResponseTimeout`. The file transport keeps ownership of late cleanup and removes only an unchanged request payload published by that exchange before a following command can publish; it does not publish `abort` and cannot retract a command already consumed by the equipment.

The Live toolbar can open the configured exchange directory and provides two commissioning helpers. The one-frame helper observes an active `command: "frame"` request and publishes one correlated mosaic-image response. The continuous toggle watches successive frame correlations and generates a fresh 768×512 response image for each one, while refusing to overwrite a response already present for that correlation. Once the UI has decoded a simulated frame, the previous generated file is released as the next request arrives, keeping this long-running test path bounded; remaining app-owned files are removed at shutdown or the next startup. These helpers intentionally publish outside the exchange lock because the real transport holds that lock while awaiting the simulated response.

Immediately after a capture response, the Desktop secures the equipment-owned `image_path` as an untranscoded, app-owned snapshot under a dedicated LocalAppData directory. Stable metadata/copy checks and WIC validation are bounded and retried; both preview and SaveFileDialog copy then use that identical snapshot, so equipment replacement or deletion cannot change the saved bytes. The snapshot is deleted after the operation, all owned snapshots are deleted when the host is disposed, and orphaned files from an abnormal exit are removed at the next startup. This directory and filename pattern are deliberately separate from response-simulator images. Saving publishes a same-extension local copy through a same-directory temporary file, so a failed copy does not truncate the chosen destination.

UNC and mapped-drive reads are performed off the UI thread and observe cancellation between buffered reads and bounded retries. Windows 7/SMB network providers cannot always cancel an in-progress file-open call; cancellation therefore stops awaiting that worker so UI shutdown can continue, while the background open may remain until the operating system's network timeout. Its staging file is cleaned when the worker returns or, after process exit, by the next startup's orphan cleanup. Deployments using network image paths should still configure practical Windows/SMB availability timeouts and verify behavior with the operator account.

## Persistence and diagnostics

Workflow documents use a schema-versioned `.drillflow.json` representation and support nested nodes. Current-run results and exchange payloads remain in memory only. Serilog rolling files are diagnostic logs, not a resumable run archive. After a crash, the operator reopens the last explicitly saved design; an uncertain physical operation is never resumed automatically.
