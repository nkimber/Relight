# Initial shell checkpoint

**Date:** 27 September 2026  
**Scope:** First runnable WPF/tray shell and initial public repository.  
**Product stage:** Development preview; no monitoring or automatic recovery.

This page records the **initial shell checkpoint** at commit `7c31f22`. Subsequent executable-host and WPF status wiring are recorded in [engine-checkpoint.md](engine-checkpoint.md); the statements below describe the initial shell only.

**Repository:** [nkimber/Relight](https://github.com/nkimber/Relight), public, default branch `main`. Initial source commit: `7c31f22`. Local and remote commit identities matched after push. The [Windows build workflow](https://github.com/nkimber/Relight/actions/workflows/build.yml) provides current CI results.

The user requested the shell and GitHub checkpoint before continuing the complete engine-first sequence. This is a bounded initial slice of M1/M5. M0 target validation and the remaining milestone criteria are not implicitly complete.

## Delivered

- .NET 10 solution and WPF/MVVM shell with Applications, History and Settings pages.
- Honest first-run empty state, disabled future controls and visible preview status.
- Original flame artwork, resident notification-area icon and context menu.
- Close/hide to tray, `--tray` startup, explicit exit confirmation and same-session single-instance activation.
- Keyboard navigation/shortcuts and high-contrast palette support.
- Build instructions, a Windows build workflow and repeatable process-level smoke checks.

## Verification evidence

Environment: Windows 11 x64, OS build 26200, .NET SDK 10.0.102 / Desktop Runtime 10.0.2. No target application or valuable job was stopped for these checks.

| Check | Result | Evidence / limits |
|---|---|---|
| Release build | Passed | `dotnet build Relight.slnx -c Release`; zero warnings/errors, warnings treated as errors. |
| Self-contained x64 preview | Passed | `dotnet publish` produced `artifacts/preview`; the same smoke checks passed against its executable. A clean machine without .NET has not been tested yet. |
| Tray-only startup | Passed | `scripts/Test-Shell.ps1`: process stays alive with no main window. |
| Second launch | Passed | Smoke check: secondary exits with code 0; primary exposes its existing dashboard. |
| Restart after interruption | Passed | Smoke check terminates only its own primary preview and starts a new resident instance successfully. |
| Applications, History, Settings rendering | Passed | Inspected live UI and accessibility tree; correct empty states and unavailable controls. |
| Keyboard navigation | Passed | Ctrl+2 and Alt+S select History and Settings; page headings/content update. |
| Hide to tray / reopen | Passed | Ctrl+W leaves the original process alive with main-window handle zero; second launch reopens it. |
| Exit confirmation / Cancel | Passed | Ctrl+Q opens the informational dialog, with Cancel selected by default; Escape returns to the dashboard. |
| Normal exit | Passed | Selected OK using the keyboard; the preview process ended normally. |
| Pointer navigation / tray menu interaction | Not verified | Automation pointer actions did not reliably activate controls, including native buttons. Requires a direct interactive check. |
| Explorer restart | Not run | Framework tray restoration is used, but Explorer was not restarted in the user's active desktop session. AT-22 is partial. |
| Full accessibility, high contrast, 100–200% scaling | Not run | Infrastructure is present; full review remains M6/M8. |
| Recovery/identity/persistence acceptance | Not run | Engine and target adapters are not implemented. |

The process-level interruption check is not full AT-29: there are no monitored targets yet. These results establish a shell checkpoint, not readiness for unattended recovery.

On 27 September 2026, the later `0.1.0` development preview was published again as a self-contained `win-x64` folder on this Windows 11 x64 desktop. It contained 261 files (about 155 MiB), including the .NET runtime and WPF assemblies. `pwsh -File scripts/Test-Shell.ps1 -Executable artifacts/preview/Relight.exe` passed tray startup, second-instance activation and restart after interruption against the published executable. This confirms the local publish/run path only; a clean machine without .NET, upgrade/removal, startup registration, full UI and real-target recovery are not validated by it.

## Next work

Return to target validation and the executable recovery model in M0/M1. Finish the outstanding shell interaction checks as part of M5; retain the engine's durable budget and identity gates before enabling protection controls.
