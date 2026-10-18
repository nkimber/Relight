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

The complete recovery suite, actual-target pilot and soak were not rerun for this storage increment. Existing recovery acceptance gates remain open. S2 offender scans, growth history, S3 reviewed cleanup and S4 automatic deletion remain planned and unimplemented; so do snooze, reminder cadence, forecasts, mounted-folder volumes and removable/network drive support.
