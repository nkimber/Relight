# Initial architecture decisions

**Status:** WPF shell starts the executable recovery host and supports basic executable registration; target integration and remaining controls are open.  
**Decision date:** 27 September 2026

## Platform and toolchain

Relight uses C# and WPF on Windows 11 x64. .NET 10 is the selected LTS family ([Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)). The initial SDK baseline is the installed 10.0.102, pinned in `global.json` with patch roll-forward. This is a reproducible development baseline, not a claim to use the newest servicing release. Refresh the SDK/runtime servicing baseline before production distribution.

The solution uses the SDK's `.slnx` format, nullable analysis, deterministic builds and warnings as errors. The Windows adapter uses Microsoft's `System.Management` package for process metadata; no third-party runtime package has been added. CI builds on Windows; running the shell requires a Windows interactive session.

## Shell and application lifetime

`Relight.App` is the single executable. `App` is the composition root and owns the dashboard, tray service and singleton. `ShutdownMode.OnExplicitShutdown` separates process lifetime from window visibility. Closing the dashboard cancels window close and hides the retained window. Explicit exit shows an informational confirmation, then disposes the tray and singleton resources.

`ShellViewModel` owns page selection, navigation commands and presentation of host snapshots. It contains no process discovery or recovery policy. `App` starts `RecoveryApplicationHost` off the dispatcher after claiming the session singleton, refreshes status through a dispatcher timer, and cancels and awaits monitoring on explicit exit. Closing the dashboard only hides it. The host creates empty configuration on a genuine first run and records operational events. The Add dialog delegates executable inspection and registration to the host. Per-profile Start now, pause/resume and confirmed reset pass through the host to the serialized coordinator; disk and process work stays off the dispatcher. Policy editing and history browsing remain open. No startup-registration changes are made.

For a new executable profile, the host validates its canonical path and current-session discovery, then creates the initial durable recovery state before saving enabled configuration with a revision check. It adds the coordinator to the live scheduler only after configuration commits. A failed save can leave an unreferenced state file, deliberately preserved rather than reused as a fresh budget. The default waits for the first user launch if no target is present.

`--shell-test` bypasses the host solely for the destructive process-level shell smoke check. Normal launches, including `--tray`, start monitoring. The smoke check uses this mode so its forced process interruption cannot affect a configured target.

## Windows integration

- Use the framework's Windows Forms `NotifyIcon` inside the WPF application for tray integration. Its menu provides dashboard/history/exit actions and disabled future controls. This avoids a third-party tray package for the initial shell. `NotifyIcon` supplies Explorer `TaskbarCreated` handling; actual Explorer restart verification is still pending.
- Use a named mutex and auto-reset event in the Windows `Local` namespace, with the current user's SID in the object names. The namespace scopes instances to the interactive session; the event activates the existing dashboard. Creating the event before acquiring the mutex preserves a second-launch signal during startup.
- Construct/release the mutex on the dispatcher thread. The registered thread-pool wait posts activation to the dispatcher, with no polling loop. A secondary instance signals then exits. Abandoned mutex ownership permits restart after interruption.
- Use an original vector flame in WPF and a multi-resolution `.ico` for the tray and executable. `scripts/New-AppIcon.ps1` reproduces the icon from the documented Bezier geometry; keep the SVG, WPF drawing and generator coordinates consistent when editing the artwork.
- Use resizable layout, scrolling, keyboard shortcuts, native control semantics and a high-contrast palette override. Full light/dark theme preferences and assistive-technology acceptance remain later work.

## Boundaries for subsequent work

The domain, coordination, Windows adapters and storage components are separate projects. `RecoveryApplicationHost` composes executable profiles from existing configuration and state; it does not create a new budget for an existing profile. Recovery policy remains independent of `Relight.App`, WPF and tray callbacks. The host still needs session-scoped state coordination, packaged-app support and most UI commands.

The shell singleton only prevents duplicate UI processes within a user session. It does **not** implement shared configuration coordination, persisted lockout across sign-ins, target-process identity or a durable attempt ledger. Those decisions and implementations remain M0–M3 work.

The actual target (ChatGPT desktop, Codex desktop or both), installed-app activation contract and job-continuation behavior have not been validated. Advancing the shell under the user's explicit request does not mark target-validation or engine milestones complete.
