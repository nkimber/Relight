# Application and scan diagnostics

Application diagnostics are enabled by default from primary-process startup. Open **Storage → Open diagnostic logs**, or **Settings → Local data → Open diagnostic logs**. Normal logs are under `%LOCALAPPDATA%\Relight\Logs\Diagnostics`; isolated previews use their own data directory. Restart the preview after rebuilding to activate new instrumentation.

After a recurrence, retain that folder promptly or use **Export diagnostic bundle** after restarting. The local ZIP includes `application-diagnostics.jsonl` (latest 500 valid records, at most 2 MiB) alongside the existing recovery events and redacted settings. Nothing is uploaded. Keep the original JSONL files when investigating a long scan because the export is a bounded recent excerpt.

## Completed scans with partial coverage

`ScanFinished` with stage `Completed` means the operation finished; its coverage can still be incomplete. Check the saved result's `TraversalComplete`, `EntryLimitReached` and `SkippedEntries` together. A finished traversal below its budget with skipped entries legitimately displays **PARTIAL SCAN — COVERAGE INCOMPLETE**. Protected/changed paths and intentional reparse/offline/recall exclusions remain gaps. Only the first 100 detailed issues are retained; they are a sample, not a breakdown of every skip.

The [1 November long-path investigation and correction](storage-checkpoint.md#long-path-coverage-correction--1-november-2026) confirmed that existing paths beyond the legacy Windows length limit could incorrectly report path-not-found. The corrected adapter uses normalized extended paths for native opens. Restart with the rebuilt application and explicitly rescan to replace older results; changing the entry budget cannot repair that historical coverage.

## Evidence

Each JSONL record has UTC time, a process-lifetime session GUID, PID, sequence and cumulative lost-record count. Startup identifies application version, build configuration, module MVID, runtime, OS, architecture, processor count and Windows session. Ten-second samples capture working set/peak, private and managed memory, GC heap/fragmentation/load/pause/allocation/collection counts, cumulative CPU, handles, process threads and thread-pool availability/backlog.

Each scan has a GUID and entry budget. Stage records distinguish waiting for the worker, traversal, worker completion awaiting the UI, persistence, comparison and presentation. Samples include inspected/measured/skipped/alias counts, identity-set size, traversal depth, elapsed/stage/progress age, cancellation, coverage and budget-stop state. The last metadata read or directory-enumeration operation records whether it is pending and for how long. Instrumentation brackets native reads and enumeration; no per-file disk log is written. First five read exceptions per scan include exception type, HRESULT/native code and up to 20 method/IL frames; later failures retain aggregate counters/latest type/code. Unhandled dispatcher/domain exceptions and unobserved task exceptions receive the same redacted detail. Exceptions are not marked handled by logging.

A background-priority dispatcher probe has at most one outstanding callback. An outstanding probe delayed at least 20 seconds emits `DispatcherDelayed`; a later reply emits `DispatcherRecovered`. This is evidence of delayed dispatch, not proof of a hang: debugger suspension, machine sleep or severe resource starvation can delay replies too. A scan `AwaitingUi` marker is emitted on the worker before returning to the dispatcher, helping separate completed traversal from delayed UI work.

Raw scanned paths, error messages, file contents, credentials, command arguments and environment contents are excluded. Paths become 16-hex SHA-256 correlation tags plus drive letter and a small allowlist of file suffixes. Tags permit comparison within evidence; they are not reversible path lookup. This also means logs alone may identify a slow category/operation without identifying a particular private filename. Method names and IL offsets need the matching build/MVID for deeper analysis. These are telemetry records, not heap dumps or live hung-thread stack captures.

## Bounds and failures

One process-wide background diagnostics thread performs sampling and file writes independently of the WPF dispatcher and shared thread pool. This deliberate exception to ordinary shared-scheduler work prevents UI/thread-pool starvation from stopping diagnostics; it is not a thread per monitored profile. Producers only update bounded state or enqueue records and never wait for log I/O. The queue holds 256 records, each capped at 32 KiB. Sampling also occurs on phase changes.

Owned `application-<session GUID>-<segment>.jsonl` files rotate at 2 MiB, retaining at most four segments per process lifetime, with a seven-day/32 MiB soft aggregate retention target. Cleanup only touches exact owned filenames, skipping reparse files. Active locked files or filesystem failures may prevent retention and exceed the aggregate target; exported bundles are unaffected. Each appended line is flushed through to disk. A partial failed write moves to a new segment; export skips malformed tails. No configuration, scan baseline, cleanup history or arbitrary user file is deleted by this retention.

Queue loss, write/capture failures and blocked retention appear in Settings status and a global degraded banner while the UI is responsive. Lost-record counts remain visible after writes recover. Writes retry on later records; a full/unavailable disk still limits evidence. Normal shutdown drains with a two-second bound and writes `ProcessStopped`; fatal exception hooks request a best-effort 250 ms flush. Forced kill cannot run shutdown hooks. Previously flushed records survive, but the newest samples may be absent. A missing stop marker means an incomplete session, not a proven crash or a root cause.

## Verification and limits — 29 October 2026

Six deterministic tests cover dispatcher delay/recovery with one pending probe, correlated scan counters, bounded issue sampling, queue overflow, write-failure recovery, owned-file rotation/age retention, malformed-tail handling, export bounds and message/path redaction. A disposable process probe creates its own blocked STA dispatcher and pending metadata marker, waits for flushed live evidence, terminates only its child and verifies retained redacted records without a stop marker. It does not run a real C-drive scan or block real filesystem I/O.

Run the probe after a Release build:

```powershell
tools/Relight.DiagnosticsProbe/bin/Release/net10.0-windows/Relight.DiagnosticsProbe.exe artifacts/diagnostics/new-isolated-run
```

Use a fresh evidence directory. The report and JSONL are ignored local artifacts. Full-drive endurance, original freeze reproduction/root cause, debugger-hang stack capture, sleep behavior and log-write latency on a failing disk remain unverified. This increment adds evidence and does not claim to fix the reported twenty-minute freeze. Real cleanup consent and settings are unchanged.
