# Relight

A Windows tray application that will watch selected desktop apps and bring them back when they stop.

[Public repository](https://github.com/nkimber/Relight)

**Current status: WPF development preview (0.1.0) with executable recovery and initial ChatGPT package support.** The resident shell starts the host and shows live profile status. You can browse for, drop, or select a running unpackaged executable, add launch arguments one per line and a working directory, or select the installed ChatGPT package, verify whether it is already running, and add it with a durable recovery ledger. ChatGPT Classic is excluded. The default waits for the first user launch when the target is absent; setup can explicitly enable initial automatic start. Per-profile Start now, pause/resume, enable/disable, remove, disabled duplicate, Test launch, policy and executable launch-identity editing, and explicit recovery reset are available; Stop and pause and Restart now are currently limited to verified executables. The History page can summarize, browse and export local events; Settings can export a redacted local diagnostic bundle and configure current-user sign-in startup. Best-effort tray notifications for stable automatic recovery and lockout can be toggled per profile. ChatGPT recovery from absence, packaged-app stopping, complete overnight diagnostics, interactive notification delivery and full sign-in verification remain unverified or in development; this is not ready for unattended protection of a valuable job.

## Build and run

Requirements: Windows 11 x64 and the .NET 10 SDK selected by [global.json](global.json). The initial build uses SDK 10.0.102, with patch roll-forward within that SDK feature band. The WPF preview has no third-party runtime NuGet packages. The Windows executable adapter uses Microsoft's `System.Management` package; the test project uses xUnit and the Microsoft test SDK.

```powershell
dotnet restore Relight.slnx
dotnet build Relight.slnx --configuration Release --no-restore
dotnet run --project src/Relight.App --configuration Release --no-build
```

Or run `src\Relight.App\bin\Release\net10.0-windows\Relight.exe` after building. That output needs the .NET 10 Desktop Runtime. Start with `--tray` to leave the dashboard hidden initially.

Ordinary launch now uses per-logon-session live state and a shared durable attempt budget. An existing legacy profile is migrated under a guarded ownership transfer; damaged or conflicting evidence suspends its automatic actions. For controlled shared-session testing, start the executable with `--shared-session-preview <absolute-data-directory> --tray`. The directory must differ from normal `%LOCALAPPDATA%\Relight`; the preview does not migrate legacy recovery state and disables sign-in startup controls. Use a disposable directory and session. `pwsh -File scripts/Test-SharedSessionPreview.ps1` checks isolated WPF startup and second-instance activation without configuring or closing a target. Real two-sign-in acceptance remains open.

## Use the preview

- Navigate Applications, History and Settings, or press **Ctrl+1**, **Ctrl+2** and **Ctrl+3**.
- Choose **Add application**, browse to the actual `.exe`, drop one `.exe` into the dialog, or choose a running unpackaged program from the current Windows sign-in. The running-process and drop paths perform Detect now automatically; a browsed path needs **Detect now** before **Add and protect**. A running match is adopted for observation; an absent target waits for your first launch unless you explicitly select and confirm initial automatic start. Adding does not close the target. Duplicate enabled identities are rejected. Packaged applications cannot be registered by their versioned WindowsApps executable path; use the supported installed-app choice.
- Use **Start now** on a configured application to check for a matching instance and launch it if absent. This explicit launch does not consume an automatic attempt or clear an existing lockout. **Restart now** pauses protection, closes the selected verified executable gracefully (with a separate force-close choice if needed), confirms absence, then makes an explicit launch. A replacement that appears before dispatch is adopted instead. Restart preserves the automatic attempt count and lockout. **Stop and pause** closes the verified target with the same graceful/force choice but leaves protection paused. Use **Pause protection** or **Resume protection** without closing the target or clearing its recovery budget. **Reset recovery** explicitly clears the episode attempt count after a confirmation; if the target is absent, a new automatic launch can follow absence confirmation and the retry delay.
- **Disable protection** stops monitoring that profile and leaves its application running; **Enable protection** restores monitoring from its existing recovery ledger. If initial automatic start is configured with a nonzero attempt limit, enabling asks you to confirm that an absent application may start. **Remove profile** deletes its configuration after confirmation but leaves the application, event history and recovery-state evidence in place. **View history** selects the profile in History.
- If an external edit damages the current configuration but the last-good copy is valid, Relight shows that copy with automatic actions suspended. In **Settings**, **Restore last-good configuration** preserves the invalid file and restarts monitoring from the backup after confirmation. Review the restored profile settings before leaving protection unattended.
- **Start Relight when I sign in** is available in Settings and the tray menu. It writes a current-user Windows startup entry for this `Relight.exe` with `--tray`, requiring no administrator setting. Moving a portable copy may leave an older path registered; the control can update that recognized Relight entry. Relight reports a conflicting entry or a mismatch between its saved preference and Windows registration instead of silently replacing it.
- The tray menu's **Pause all** and **Resume all** act on eligible enabled profiles independently. A failure on one profile is reported with a count and does not prevent the others from being updated. These actions preserve recovery budgets and leave target applications running.
- The tray tooltip summarizes protected, observing, paused and alert counts. Its icon prioritizes intervention or detection trouble, then recovery/observation, healthy monitoring, and all-paused or disabled status. Open the dashboard for the full state explanation.
- Dashboard rows show current automatic attempts, whether an instance has been verified in this session, when it was last verified, and a live next-action explanation. Retry and appearance countdowns describe eligibility/checks, not a promised launch; an overdue stability check is labeled unverified.
- In **Applications**, filter by attention, recovering, protected or paused/disabled status, and sort by name, status priority or attempts used. Filters affect the display only; they do not change which profiles Relight monitors.
- **Add application** offers a choice to start automatically if the selected target is initially absent. The dialog previews the default recovery policy and asks for confirmation before saving that choice. **Edit profile** changes the name, executable path, literal launch arguments, working directory, monitoring/recovery settings and notification preferences. Changing an executable identity leaves the old process running, preserves the recovery budget and starts fresh discovery for the new target. The installed ChatGPT identity remains fixed. The editor shows units, allowed ranges and a live plain-language policy preview. **Test launch** uses the saved target: it checks for a matching instance before dispatch, then reports the instance discovered or an appearance timeout. Save launch-setting edits before testing. Saving an enabled profile asks for confirmation when the edit could allow automatic initial start of an absent application. Changing observation duration restarts the current stability timer; changing retry delay sets a new deadline from Save. A launch already in progress keeps its original timeout and attempt policy and must finish reconciliation before an identity change is accepted.
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
pwsh -File scripts/Test-ConfigurationLease.ps1
pwsh -File scripts/Test-HostConfigurationRace.ps1
pwsh -File scripts/Test-SharedBudgetRace.ps1
pwsh -File scripts/Test-MigrationInterruption.ps1
pwsh -File scripts/Test-LogRetentionLease.ps1
```

The shell smoke check uses `--shell-test` to disable monitoring while it starts and interrupts its own preview processes. It verifies hidden tray startup, second-instance activation, and restart after interruption. The test suite runs deterministic recovery/storage checks and controlled Windows process tests. The legacy migration probe requires the local Git history containing `bbd5879`; it builds that pre-marker storage/engine version in an ignored `artifacts` directory and verifies that its separate process cannot write after migration and an already-reserved fake launch remains charged without a second dispatch. The configuration, shared-budget and log-retention probes exercise cross-process file coordination. Interactive checks and outstanding coverage are recorded in [the shell checkpoint](docs/shell-checkpoint.md), [engine checkpoint](docs/engine-checkpoint.md) and [history checkpoint](docs/history-checkpoint.md).

The 20-profile idle CPU and closed-dashboard working-set probe is described in [the performance checkpoint](docs/performance-checkpoint.md). It uses isolated preview data and a 15-minute measurement interval; it does not replace the full M8 and M9 acceptance runs.

A self-contained preview can be produced locally with:

```powershell
dotnet publish src/Relight.App -c Release -r win-x64 --self-contained true -o artifacts/preview
pwsh -File scripts/Test-Shell.ps1 -Executable artifacts/preview/Relight.exe
```

To create a versioned portable preview ZIP with dependency notices, a manifest and SHA-256 checksum from a clean committed checkout, run `pwsh -File scripts/Build-PortablePreview.ps1`. It writes a new directory under ignored `artifacts/releases` and smoke-checks the published executable before archiving it. See [the release checkpoint](docs/release-checkpoint.md) for verified scope and remaining release gates.

For a controlled disposable-target soak, build the Release solution and run `dotnet run --project tools/Relight.SoakProbe -c Release -- 1440`. This runs two isolated profiles for 24 hours and leaves progress and result JSON under ignored `artifacts/soak`; see [the soak checkpoint](docs/soak-checkpoint.md). It does not exercise ChatGPT or open/close the dashboard.

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
