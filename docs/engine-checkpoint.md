# Recovery foundation checkpoint

**Date:** 27 September 2026  
**Scope:** Portions of M0–M2; not a working watchdog or release candidate.

## Implemented

- `Relight.Core` contains policy validation, lifecycle states, observation provenance, transition results and a per-profile recovery model driven by caller-supplied monotonic elapsed time and discovery outcomes.
- The model handles first-start adoption, absence confirmation, retry deadlines, exact reservation caps, appearance timeout, early exit, intervention lockout, external-start observation and rearming, pause/unavailable gaps, policy edits, explicit starts and stale launch operations.
- The model exports a checkpoint without process identity or monotonic deadlines. Restore conservatively requires fresh discovery, preserves reserved attempts and lockout, and marks an interrupted final reservation locked out pending stable observation or explicit reset.
- `Relight.Storage` writes versioned, checksummed, revision-checked per-profile state snapshots. It flushes a temporary file before atomic replacement and keeps a prior backup. Corrupt, missing, unsupported or stale state fails closed; it does not silently load a backup and refund an attempt.
- `Relight.TestTarget` is a controllable Windows Forms process that can exit on a timer, spawn a helper, hand off from a short-lived launcher, accept or refuse graceful close, and report its PID/start time to a disposable ready file.
- The test suite covers deterministic recovery boundaries, restart reconstruction, damaged state and real Windows process behaviors. The Windows build workflow runs deterministic tests; process/window tests run in an interactive local session.

## Verification and limits

`dotnet test Relight.slnx -c Release` passed **23 tests** on the combined tree: 19 deterministic recovery/storage scenarios and 4 interactive Windows test-target scenarios. The GitHub workflow runs the deterministic subset; its first run with these changes is pending publication.

The recovery model and state store are not yet connected to the WPF shell or to real process discovery and launch. No automatic target launch can occur from this code today. Configuration persistence, operational event logs, notifications, packaged-app activation, session-coordinated lockout and the UI editor/history remain open. M1 and M2 exit criteria are therefore not claimed complete.

State writes are designed to survive application interruption. A separate power-loss and cross-session validation gate remains: flushing the temporary file and replacing it atomically are not proof against every storage device or OS failure mode.
