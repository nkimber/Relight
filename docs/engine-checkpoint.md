# Recovery foundation checkpoint

**Date:** 27 September 2026  
**Scope:** Portions of M0–M3; not an active watchdog or release candidate.

## Implemented

- `Relight.Core` contains policy validation, lifecycle states, observation provenance, transition results and a per-profile recovery model driven by caller-supplied monotonic elapsed time and discovery outcomes.
- The model handles first-start adoption, absence confirmation, retry deadlines, exact reservation caps, appearance timeout, early exit, intervention lockout, external-start observation and rearming, pause/unavailable gaps, policy edits, explicit starts and stale launch operations.
- The model exports a checkpoint without process identity or monotonic deadlines. Restore conservatively requires fresh discovery, preserves reserved attempts and lockout, and marks an interrupted final reservation locked out pending stable observation or explicit reset.
- `Relight.Storage` writes versioned, checksummed, revision-checked per-profile state snapshots. It flushes a temporary file before atomic replacement and keeps a prior backup. Corrupt, missing, unsupported or stale state fails closed; it does not silently load a backup and refund an attempt.
- `Relight.Storage` also has separate versioned configuration with explicit first-run initialization, validated profile/global settings, atomic replacement, a last-good backup, revision plus content-hash concurrency checks, and conservative enabled-profile overlap validation. If the current file is damaged or externally edited into an invalid form, the backup can be displayed with automatic actions suspended. Repair UI and multi-session coordination are pending.
- `Relight.TestTarget` is a controllable Windows Forms process that can exit on a timer, spawn a helper, hand off from a short-lived launcher, accept or refuse graceful close, and report its PID/start time to a disposable ready file.
- `Relight.Engine` now provides a per-profile coordinator with serialized ticks and pause commands. It discovers twice before an automatic dispatch, durably reserves the attempt, prevents a launch if the write fails, and lets independent profiles progress while another launch blocks. It has no shared scheduler or application-lifetime host yet.
- `Relight.Windows` provides an executable launch/discovery adapter. Launch uses `ProcessStartInfo.ArgumentList`; discovery requires an exact canonical executable path, the current Windows user SID and session, and records PID plus start time. Optional exact argument selectors distinguish a main process from a same-path helper. Ambiguous or inaccessible candidates produce unavailable detection. This adapter does not activate installed apps or establish a logical handoff contract.
- The test suite covers deterministic recovery boundaries, restart reconstruction, damaged state and real Windows process behaviors. The Windows build workflow runs deterministic tests; process/window tests run in an interactive local session.

## Verification and limits

`dotnet test Relight.slnx -c Release --no-restore` passed **36 tests** on this Windows 11 x64 desktop: 29 deterministic recovery/storage/coordinator scenarios and 7 controlled Windows process scenarios. The GitHub workflow runs the deterministic subset. The configuration increment has not yet been verified by a GitHub workflow at the time of this checkpoint.

The coordinator and executable adapter are not yet connected to a scheduler or the WPF shell. The integration test can launch the disposable test target; the Relight preview cannot automatically launch a user target. Configuration storage is isolated but is not yet wired to an application host or repair UI. Operational event logs, notifications, packaged-app activation, session-coordinated lockout, user controls and the UI editor/history remain open. M1–M3 exit criteria are therefore not claimed complete.

The Windows adapter depends on Microsoft's `System.Management` 10.0.11 package for process command lines and owner SID checks. Its [NuGet package page](https://www.nuget.org/packages/System.Management/10.0.11) identifies the Microsoft MIT license; carry its notice into release dependency review. WMI inspection and exact executable paths can become unavailable after an application update. Neither packaged target has been controlled by this adapter.

State writes are designed to survive application interruption. A separate power-loss and cross-session validation gate remains: flushing the temporary file and replacing it atomically are not proof against every storage device or OS failure mode.
