# Relight

A Windows tray application that will watch selected desktop apps and bring them back when they stop.

[Public repository](https://github.com/nkimber/Relight)

**Current status: runnable WPF shell preview (0.1.0).** The dashboard, tray lifetime and single-instance activation work. Application registration, monitoring, recovery, persistence, notifications and sign-in startup are not implemented yet. This preview does not control or monitor other applications.

## Build and run

Requirements: Windows 11 x64 and the .NET 10 SDK selected by [global.json](global.json). The initial build uses SDK 10.0.102, with patch roll-forward within that SDK feature band. No third-party NuGet packages are required.

```powershell
dotnet restore Relight.slnx
dotnet build Relight.slnx --configuration Release --no-restore
dotnet run --project src/Relight.App --configuration Release --no-build
```

Or run `src\Relight.App\bin\Release\net10.0-windows\Relight.exe` after building. That output needs the .NET 10 Desktop Runtime. Start with `--tray` to leave the dashboard hidden initially.

## Use the preview

- Navigate Applications, History and Settings, or press **Ctrl+1**, **Ctrl+2** and **Ctrl+3**.
- Close the dashboard or press **Ctrl+W** to hide it and keep Relight resident.
- Double-click the flame in the notification area, choose **Open dashboard** from its menu, or launch Relight again to return to the existing instance. Windows may place the icon in its tray overflow.
- Choose **Exit Relight** or press **Ctrl+Q** to quit. The confirmation defaults to Cancel. Other applications stay running.
- Unimplemented controls are disabled and labeled. No profiles, activity logs or startup registration are written by this preview.

## Verify

Exit an already-running preview, then run:

```powershell
pwsh -File scripts/Test-Shell.ps1
```

The smoke check verifies hidden tray startup, second-instance activation, and startup after a forced interruption. It cleans up only the preview processes it starts. Interactive checks and outstanding coverage are recorded in [the shell checkpoint](docs/shell-checkpoint.md).

A self-contained preview can be produced locally with:

```powershell
dotnet publish src/Relight.App -c Release -r win-x64 --self-contained true -o artifacts/preview
```

This is a development preview, not the production release or overnight pilot described in the PRD.

## Project layout

| Path | Contents |
|---|---|
| `src/Relight.App` | WPF shell, view model, tray integration and session singleton |
| `scripts` | Repeatable shell checks and original icon generation |
| `docs` | Requirements, delivery plan, architecture and verification evidence |
| `AGENTS.md` | Engineering and recovery rules for contributors/agents |

Start with [the docs index](docs/index.md) and [development plan](docs/development-plan.md). The recovery engine will remain independent of WPF; no placeholder engine projects have been added.

The flame artwork is original to this repository. WPF and Windows Forms are provided by the Microsoft .NET Windows Desktop framework; no Resurrector code or assets are copied into the shell.
