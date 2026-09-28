# Relight

A Windows tray application that will watch selected desktop apps and bring them back when they stop.

[Public repository](https://github.com/nkimber/Relight)

**Current status: WPF development preview (0.1.0) with executable recovery and initial ChatGPT package support.** The resident shell starts the host and shows live profile status. You can browse for an executable, add launch arguments one per line and a working directory, or select the installed ChatGPT package, verify whether it is already running, and add it with a durable recovery ledger. The default waits for the first user launch when the target is absent. Per-profile Start now, pause/resume, enable/disable, remove, disabled duplicate, policy editing and explicit recovery reset are available; Stop and pause and Restart now are currently limited to verified executables. The History page can summarize, browse and export local events; Settings can export a redacted local diagnostic bundle and configure current-user sign-in startup. Best-effort tray notifications for stable automatic recovery and lockout can be toggled per profile. ChatGPT recovery from absence, packaged-app stopping, launch-identity editing, complete overnight diagnostics, interactive notification delivery and full sign-in verification remain unverified or in development; this is not ready for unattended protection of a valuable job.

## Build and run

Requirements: Windows 11 x64 and the .NET 10 SDK selected by [global.json](global.json). The initial build uses SDK 10.0.102, with patch roll-forward within that SDK feature band. The WPF preview has no third-party runtime NuGet packages. The Windows executable adapter uses Microsoft's `System.Management` package; the test project uses xUnit and the Microsoft test SDK.

```powershell
dotnet restore Relight.slnx
dotnet build Relight.slnx --configuration Release --no-restore
dotnet run --project src/Relight.App --configuration Release --no-build
```

Or run `src\Relight.App\bin\Release\net10.0-windows\Relight.exe` after building. That output needs the .NET 10 Desktop Runtime. Start with `--tray` to leave the dashboard hidden initially.

## Use the preview

- Navigate Applications, History and Settings, or press **Ctrl+1**, **Ctrl+2** and **Ctrl+3**.
- Choose **Add application**, browse to the actual `.exe`, select **Detect now**, then **Add and protect**. A running match is adopted for observation; an absent target waits for your first launch. Adding does not start or close the target. Duplicate enabled identities are rejected.
- Use **Start now** on a configured application to check for a matching instance and launch it if absent. This explicit launch does not consume an automatic attempt or clear an existing lockout. **Restart now** pauses protection, closes the selected verified executable gracefully (with a separate force-close choice if needed), confirms absence, then makes an explicit launch. A replacement that appears before dispatch is adopted instead. Restart preserves the automatic attempt count and lockout. **Stop and pause** closes the verified target with the same graceful/force choice but leaves protection paused. Use **Pause protection** or **Resume protection** without closing the target or clearing its recovery budget. **Reset recovery** explicitly clears the episode attempt count after a confirmation; if the target is absent, a new automatic launch can follow absence confirmation and the retry delay.
- **Disable protection** stops monitoring that profile and leaves its application running; **Enable protection** restores monitoring from its existing recovery ledger. **Remove profile** deletes its configuration after confirmation but leaves the application, event history and recovery-state evidence in place. **View history** selects the profile in History.
- If an external edit damages the current configuration but the last-good copy is valid, Relight shows that copy with automatic actions suspended. In **Settings**, **Restore last-good configuration** preserves the invalid file and restarts monitoring from the backup after confirmation. Review the restored profile settings before leaving protection unattended.
- **Start Relight when I sign in** is available in Settings and the tray menu. It writes a current-user Windows startup entry for this `Relight.exe` with `--tray`, requiring no administrator setting. Moving a portable copy may leave an older path registered; the control can update that recognized Relight entry. Relight reports a conflicting entry or a mismatch between its saved preference and Windows registration instead of silently replacing it.
- The tray menu's **Pause all** and **Resume all** act on eligible enabled profiles independently. A failure on one profile is reported with a count and does not prevent the others from being updated. These actions preserve recovery budgets and leave target applications running.
- The tray tooltip summarizes protected, observing, paused and alert counts. Its icon prioritizes intervention or detection trouble, then recovery/observation, healthy monitoring, and all-paused or disabled status. Open the dashboard for the full state explanation.
- Dashboard rows show current automatic attempts, whether an instance has been verified in this session, when it was last verified, and a live next-action explanation. Retry and appearance countdowns describe eligibility/checks, not a promised launch; an overdue stability check is labeled unverified.
- In **Applications**, filter by attention, recovering, protected or paused/disabled status, and sort by name, status priority or attempts used. Filters affect the display only; they do not change which profiles Relight monitors.
- **Edit policy** changes the profile name and monitoring/recovery settings without closing the target or clearing its attempt count. The dialog shows units and allowed ranges. Changing observation duration restarts the current stability timer; changing retry delay sets a new deadline from Save. A launch already in progress keeps its original timeout and attempt policy. Launch identity remains read-only in this editor.
- Open **History** to load local recovery events. Filter by application (including removed profiles with retained events), severity, event type, episode ID and time range, then select **Refresh history**. The summary counts recorded outcomes for the selected application, episode and period; monitoring-gap time remains unknown and is never counted as confirmed downtime. Expand a row for UTC time and correlation IDs; use **Open log folder** for the original JSON Lines files. At most 500 matching rows are displayed, with a count when older matches are omitted. **Export filtered history** writes all matching retained records to a local CSV or text file you choose; it is not limited to the displayed rows.
- Close the dashboard or press **Ctrl+W** to hide it and keep Relight resident.
- Double-click the flame in the notification area, choose **Open dashboard** from its menu, or launch Relight again to return to the existing instance. Windows may place the icon in its tray overflow.
- Choose **Exit Relight** or press **Ctrl+Q** to quit. The confirmation defaults to Cancel. Other applications stay running.
- The Applications page shows configured profiles and monitoring status. Unimplemented controls are disabled and labeled. First run creates empty local configuration; profile state is stored separately under `%LOCALAPPDATA%\Relight\State`, and operational events under `Logs`. It does not register sign-in startup.

## Verify

Exit an already-running preview, then run:

```powershell
pwsh -File scripts/Test-Shell.ps1
dotnet test tests/Relight.Core.Tests/Relight.Core.Tests.csproj -c Release
pwsh -File scripts/Test-LegacyMigration.ps1
```

The shell smoke check uses `--shell-test` to disable monitoring while it starts and interrupts its own preview processes. It verifies hidden tray startup, second-instance activation, and restart after interruption. The test suite runs deterministic recovery/storage checks and controlled Windows process tests. The legacy migration probe requires the local Git history containing `bbd5879`; it builds that pre-marker storage version in an ignored `artifacts` directory and verifies that its separate process cannot write after migration. Interactive checks and outstanding coverage are recorded in [the shell checkpoint](docs/shell-checkpoint.md) and [engine checkpoint](docs/engine-checkpoint.md).

A self-contained preview can be produced locally with:

```powershell
dotnet publish src/Relight.App -c Release -r win-x64 --self-contained true -o artifacts/preview
pwsh -File scripts/Test-Shell.ps1 -Executable artifacts/preview/Relight.exe
```

This is a development preview, not the production release or overnight pilot described in the PRD.

## Project layout

| Path | Contents |
|---|---|
| `src/Relight.App` | WPF shell, view model, tray integration and session singleton |
| `src/Relight.Core` | UI-independent recovery policy and state model |
| `src/Relight.Storage` | Versioned runtime state, validated configuration and queued JSON Lines event recording |
| `src/Relight.Engine` | Per-profile coordination, durable reservations, shared polling and bounded launch dispatch |
| `src/Relight.Windows` | Executable adapter and fail-closed recovery host composition |
| `tests` | Deterministic model/store tests and controllable Windows test target |
| `scripts` | Repeatable shell checks and original icon generation |
| `docs` | Requirements, delivery plan, architecture and verification evidence |
| `AGENTS.md` | Engineering and recovery rules for contributors/agents |

Start with [the docs index](docs/index.md) and [development plan](docs/development-plan.md). Recovery policy remains independent of WPF. The dashboard displays host snapshots and supports basic executable or selected ChatGPT registration, Start now, pause/resume, enable/disable, remove, name/policy edits, reset and local event browsing/export. Stop and pause and Restart now apply to verified executables; launch-identity editing is pending.

The flame artwork is original to this repository. WPF and Windows Forms are provided by the Microsoft .NET Windows Desktop framework; no Resurrector code or assets are copied into the shell.
