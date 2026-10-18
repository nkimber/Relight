# Storage monitoring checkpoint

**Date:** 18 October 2026. **Scope:** S1 implementation checkpoint; storage acceptance remains partial. [PRD Section 18](PRD.md#18-storage-protection) defines the extension and [development plan](development-plan.md#storage-protection-extension--authorized-18-october-2026) defines subsequent increments.

## Delivered

Settings → **Disk space monitoring…** opens a modeless storage window with latest capacity, percentage, severity and local check time. Monitoring defaults to the Windows system drive every 60 seconds. Enable/disable monitoring, mute notifications, change the interval (10–3600 seconds), and add/delete fixed local drive-letter rows with individual warning/critical thresholds. An empty drive list means the system drive with defaults. Values labeled GiB use 1,073,741,824 bytes.

Warning defaults to below 10% OR 20 GiB; critical defaults to below 5% OR 5 GiB. Notifications occur on warning entry and critical escalation, once per severity in the in-memory episode. Recovery requires both warning thresholds plus 2 percentage points and 1 GiB. Critical deescalation also requires a margin. Unknown readings preserve episode state; they do not create low-space notifications. Disabling/re-enabling monitoring, changing thresholds or restarting Relight begins fresh evaluation. Muting continues sampling and does not replay an already-observed severity on unmute.

The tray shows attention for low or unavailable space; Settings retains the full explanation. Notification errors are visible with the latest readings. Windows may suppress balloon delivery, and successful API submission is not proof a person saw it. There is no storage event journal or persistent notification history in this slice.

## Architecture and persistence

`Relight.Core` owns validated settings and the deterministic threshold/episode policy. `Relight.Windows.DiskSpaceMonitor` samples `GetDiskFreeSpaceExW` available-to-caller bytes and total capacity, with an injectable reader for tests. It only accepts fixed local drive-letter roots. The WPF composition root schedules one background sampling batch independently of recovery/status refresh, without overlapping batches or catch-up bursts. Latest readings and at most 26 per-drive alert states are kept in memory; directory enumeration is absent. Native calls cannot be canceled mid-call; explicit exit does not wait for a capacity read, which performs no writes or target control.

Settings are an additive property of version-1 global configuration. Existing files without the property load system-drive defaults; explicit invalid settings follow the existing last-good/fail-closed behavior. The host serializes saves with other configuration changes and uses existing revision checks, durable atomic replacement and backup. Runtime budgets and target processes are untouched. No package dependency, service or elevation was added.

## Verification

Environment: local Windows 11 x64 interactive session, .NET SDK 10.0.401. Low-space policy uses fake readings; the user's disk was not filled and no real target was stopped.

| Check | Result and limits |
|---|---|
| Release solution build | Passed, zero warnings/errors. |
| Storage policy/native/persistence tests | Passed; exact thresholds, either-limit triggers, validation, episode suppression, unknown gap, multi-drive isolation, mute/disable, stale writes, legacy defaults, invalid-config fallback and native system-drive reading. |
| Related regression group | Passed: storage, configuration store, host configuration, recovery notifications and shutdown. All 27 tests passed, including nine storage tests and host save/restart coverage. |
| `pwsh -File scripts/Test-Shell.ps1` | Passed: resident tray startup, second launch activation and restart after interruption. Shell-test mode deliberately disables both recovery and storage. |
| `pwsh -File scripts/Test-StorageUi.ps1` | Passed in isolated empty configuration: native live reading, invalid interval rejected, interval and notification preference saved, storage/dashboard close leaves process alive, reopen restores preferences. Harness only terminates its own preview. Initial harness attempts captured a transient startup handle; waiting for the accessible dashboard resolved that harness issue. |

| Acceptance | Status |
|---|---|
| DSAT-01 | Passed at policy level. |
| DSAT-02 | Passed with fake readings; actual Windows notification display remains unverified. |
| DSAT-03 | Passed with injected failed drive plus native system-drive reading; physical multi-drive unplug/permission scenarios not run. |
| DSAT-04 | Passed at store/host level and UI save/reopen. |
| DSAT-05 | Partial: native UI save/reopen and resident process checked. Long hidden-window sampling, recovery under concurrent blocked native storage I/O, full keyboard/screen-reader and scaling review not run. |
| DSAT-06 | Partial: policy suppression and existing tray submission wired. Visual balloon delivery, notification suppression, Explorer restart and physical sleep/resume not run. |

The complete recovery suite, actual-target pilot and soak were not rerun for this storage increment. Existing recovery acceptance gates remain open. The S2 checkpoint below supersedes the initial scan deferral. S3 reviewed cleanup and S4 automatic deletion remain unimplemented, along with snooze, reminder cadence, forecasts, mounted-folder volumes and removable/network drive support.


## S2 — Offender analysis checkpoint

**Date:** 18 October 2026. This implements the next requested storage increment, with explicit remaining acceptance below. Existing recovery release gates remain open.

### Delivered and usage

Open Settings → Disk space monitoring… and use **Where is the space going?**. Enter or browse to a fixed-local-drive folder/root, then select **Scan folder**. Scanning is explicit and independent of capacity monitoring. A cancel button stops traversal between native operations; closing the Storage window also requests cancellation. Reopening loads the latest saved results without starting a new scan.

Results show the largest 100 files and folders by allocated space, logical size alongside allocation, positive logical-size growth, scan timestamp/age, progress and partial coverage. Folder totals include descendants and overlap parent totals; they must not be added together. Logical totals count every file path. Allocated totals deduplicate volume/file identity, assigning shared allocation to the first encountered hard-link path. This makes folder allocation depend on traversal order, so growth uses logical size rather than potentially reassigned allocation. File/folder rows are consumers, not cleanup recommendations.

Growth compares only the same scan root and retained paths fully measured in both scans. Newly encountered, omitted or inaccessible paths have unknown growth. The baseline timestamp and limitation are shown. Only the latest scan is saved; choosing another root replaces the single baseline. This is a bounded comparison, not complete growth history or a forecast.

### Safety, privacy and bounds

- One sequential scan worker per application process, coordinated across window lifetimes. Work runs off the dispatcher and recovery scheduler. Progress reports every 250 inspected entries. No new dependencies or elevation.
- Native directory enumeration uses verified handles with write/delete sharing; it does not hold directories against application rename/delete. Metadata-only handles use open-existing, open-reparse-point and no-recall flags, with canonical-path verification. Changed/unavailable paths are reported as skipped. No file contents are opened or read.
- Reparse points, cloud/offline/recall entries and roots beneath excluded ancestors are skipped. This deliberately omits cloud-backed folders and mounted/junction locations rather than following them. Short-name or other aliases that fail canonical-path verification are also skipped.
- Native identity uses volume plus 128-bit file ID; logical/allocation metadata is separate, with compression metadata for sparse/compressed streams. Directory storage overhead, alternate data streams and excluded entries are unmeasured. Allocation is not promised reclaimable space, and totals need not reconcile with volume-used capacity.
- Traversal stops at 100,000 inspected entries or depth 128, retaining at most 100 file rows, 2,000 folder rows and 100 issue details. The UI shows 100 rows per size category and at most 100 positive growth rows. Counts include all observed failures even when issue details are capped. Select a narrower folder after a capped scan.
- One versioned baseline of at most 4 MiB under `%LOCALAPPDATA%\Relight\Storage\latest-scan.json`, with an exclusive writer lease and one bounded owned staging file. Writes flush before atomic replacement. Cancellation before commit preserves the baseline; once a completed scan starts its short commit, that commit may finish even if the window closes. No scan task holds up application shutdown; interruption may leave the one staging file for reuse.
- Invalid/unsupported baselines are preserved and block replacement; the current scan remains visible with a save error. Scanned paths remain local, outside recovery logs and diagnostic ZIPs. No scans are uploaded. Live filesystem changes can affect measurements; this is not a point-in-time filesystem snapshot.

Windows implementation references: [directory metadata enumeration](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_full_dir_info), [file allocation metadata](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info), [compression metadata](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_compression_info), and [metadata-only/no-recall handle flags](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).

### Verification and remaining acceptance

Environment: local Windows 11 x64 / NTFS, .NET SDK 10.0.401. Tests use self-owned temporary trees; no valuable target or existing user file was modified or terminated.

| Check / acceptance | Evidence and limits |
|---|---|
| Release solution build | Passed with zero warnings/errors. |
| DSAT-07 | Passed: fake aggregation/hard-link accounting plus native hard-link and sparse-allocation checks. Native multi-buffer enumeration checks 800 Unicode-named files and permits directory rename while its enumeration handle is open. |
| DSAT-08 | Passed for injected access denial, interrupted enumeration, excluded ancestors, and native junction exclusion. Actual cloud-provider hydration, physical ACL changes and hostile path replacement were not exercised. |
| DSAT-09 | Passed for deterministic cancellation during traversal, queued cancellation preserving a baseline, entry/result/issue/depth bounds, and the shared worker gate in composition. UI cancellation of a deliberately blocked native call and full-drive responsiveness/soak were not run. |
| DSAT-10 | Passed for same-root growth, partial/missing baseline exclusion, baseline round-trip, drive-root handling, unsupported schema, corrupt evidence preservation and size limits. Physical disk-full during baseline commit was not injected. |
| DSAT-11 | Passed `pwsh -File scripts/Test-StorageUi.ps1 -ExerciseScan`: controlled folder scan, rendered growth after file expansion, saved baseline/root restored on reopen, invalid settings rejected and normal settings persistence retained; complete accessibility, scaling and recovery-under-scan stress remain not run. |

The final focused storage/configuration/notification/shutdown/diagnostics regression group passed all **42 tests**, including **12 scanner tests**. One preceding run alongside the UI harness had 41 passes and a failure in the existing `MonitoringShutdownTests.Exit_waits_for_cleanup_and_releases_its_deadline` immediate timer-disposal assertion; inspection showed that assertion observes cleanup immediately after the wait task completes. The unchanged group passed when rerun without concurrent UI work. This records a test timing limitation, not a storage or shutdown acceptance claim. The Release solution build passed with zero warnings/errors; `pwsh -File scripts/Test-Shell.ps1` passed resident startup, activation and restart after interruption. The storage UI harness initially encountered a transient configuration read lock during save; it now retries that expected read contention and the rerun passed.

The complete recovery suite, actual-target pilot, large-drive performance and overnight soak were not rerun for S2. S2 itself never deletes scanned files; subsequent cleanup work is recorded below.

## S3 — Reviewed cleanup checkpoint

Storage → Reviewed cleanup provides reusable rules for an exact folder's immediate files, with pattern/exclusions, minimum age, newest-file retention, per-run file/byte caps and an optional current-user/session executable absence guard. Supported extensions: .log, .tmp, .bak, .old, .dmp, .cache and .etl. There is no recursive deletion or directory removal. File selections default unchecked and require a separate permanent-deletion confirmation, with Cancel selected by default. Saving/removing a rule does not delete files. Automatic cleanup is absent at this checkpoint.

The service rechecks file identity, length/allocation, modification/change timestamps, attributes and link count on an exclusive DELETE handle, then deletes that same verified handle while briefly pinning canonical ancestors. Protected locations, unknown guards, incomplete/expired previews and failed durable pre-dispatch audit block execution. Changed, locked, hard-linked, reparse/cloud and protected-attribute files are skipped. Previews expire after ten monotonic minutes, are invalidated by edits and cannot be replayed. Before/after available-space change is displayed separately from allocation estimates because other disk activity can affect it.

Rule storage is versioned/revision checked, capped at 50 rules and 2 MiB, with atomic writes and a backup. The latest run is capped at 200 outcomes and 2 MiB. Pending audit records precede each dispatch; audit failure stops further files, and interrupted pending outcomes remain unknown. No prior run resumes automatically. Scan/cleanup paths stay outside recovery logs and diagnostic exports. Windows Storage settings handle OS-managed cleanup and Recycle Bin management.

Verified on local Windows 11 x64 / NTFS, SDK 10.0.401: Release build with zero warnings/errors; 32 focused tests, including 11 reviewed-cleanup tests. Native tests affected disposable files only and covered selected/unselected, changed/locked files and junction replacement. Fake adapters covered cancellation, expiry, forged selection, caps, guards and pre/post-dispatch audit failure. The post-dispatch audit test was corrected to accept either IOException or UnauthorizedAccessException for Windows destination contention; product behavior was unchanged.

`Test-StorageUi.ps1 -ExerciseCleanup` passed isolated rule-save and preview checks, preserved disposable files, and verified selections default off. UI confirmation, accessibility/scaling, real application guards, cross-sign-in contention and physical disk-full remain unverified. Immediate-file rules intentionally exclude recursive cleanup and built-in application recipes. [Windows same-handle deletion documentation](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle).

## S4 — Opt-in automatic cleanup checkpoint

Automatic cleanup remains off by default. The user subsequently requested the next part after S4 was identified; this adds the capability without enabling any real user rule. In Reviewed cleanup, save the exact rule, generate a complete preview, enter an interval (default 24 hours) and free-space trigger (default below 20 GiB), then choose **Approve automatic cleanup…**. Its default-Cancel confirmation explicitly authorizes future matching files and irreversible deletion, regardless of current file checkboxes. The approval includes an exact rule fingerprint and unique consent ID. Saving/removing any rule revokes all approvals; a separate Disable all control cancels pending local work before attempting the durable off save.

The resident worker checks once a minute independently of recovery. It requires low known capacity, a full monotonic interval in the current worker, and the durable UTC deadline. An exclusive worker lease selects one owner per data directory. Ownership takeover/restart waits the full interval again; this avoids cross-session or wall-clock changes granting an early run. At most one rule runs per pulse, with no catch-up loop. The next interval is reserved before a new preview; failures, empty previews and interrupted operations are not refunded. A shared storage gate avoids overlap with interactive scans/cleanup, and a separate run lease protects audit writes across sessions.

Before each file, age and newest-file retention are checked again, and approval is revalidated under a short configuration lease covering native deletion. Disabling or editing stops subsequent files; current native operations cannot be undone or forcibly canceled. The latest detailed run and up to 100 summaries retain origin, counts, free-space change and pending/unknown outcomes. All paths remain local and outside diagnostic bundles. Invalid/corrupt rules, approvals or audit history fail closed; originals are preserved. A failed disable save is reported rather than represented as a persisted success, while local execution remains stopped until explicit reapproval or restart with trusted saved configuration.

Verification uses local Windows 11 x64 / NTFS, SDK 10.0.401. Fake-clock tests cover defaults, explicit approval, interval/low-space/busy-work gates, worker takeover, clock jumps, durable reservation, edit/disable revocation, immediate cancellation and bounded history. A native automatic-run test affects only disposable explicitly approved files and preserves excluded files. Reviewed native tests also cover changed/locked files, junction substitution and retention changes since preview. No real user rule is enabled and no valuable file is removed during validation.

`Test-StorageUi.ps1 -ExerciseAutomaticApproval` passed: Cancel retains off; explicit approval persists without an immediate delete; Disable all persists off. The isolated fixture's saved rules are off at the end. The focused suite passed all **62 tests**, including **8 automatic-cleanup tests**, **12 reviewed-cleanup tests** and the storage/configuration/notification/shutdown/diagnostics regression group. Release solution build passed with zero warnings/errors; the shell smoke check passed. A rapid history-write test exposed a destination-access failure with overwrite-move; existing audit/history updates now use atomic replacement, matching rule/configuration writes, and the final run passed. Preflight blocks/no-candidate runs also record a bounded automatic summary and consume their reserved interval.

Real overnight scheduling, actual two-sign-in takeover, notification display, blocked native I/O shutdown, live cloud-provider behavior, physical disk-full and full accessibility/scaling remain unverified. No full recovery suite or soak was rerun. These implementation checkpoints do not complete M8/M9 or the recovery acceptance matrix.
