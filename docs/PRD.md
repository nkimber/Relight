# Relight — Product Requirements Document

**Status:** Draft requirements; WPF/tray shell and executable recovery foundation implemented, acceptance incomplete  
**Date:** 27 September 2026  
**Product name:** Relight (confirmed)  
**Platform:** Windows desktop; C# implementation  
**Initial target:** The user's ChatGPT desktop application, with support for additional applications

## 1. Product purpose

Relight is a Windows system tray application that watches selected desktop applications, detects when they stop, and restarts them according to configurable rules. It gives extra attention to newly started applications, stops automatic recovery after repeated unsuccessful attempts, and automatically restores protection when the user manually restarts an application and it remains stable.

The primary use case is unattended overnight work. In the morning, the user should be able to see whether an application stopped, when recovery was attempted, whether it succeeded, and why automatic recovery may have stopped.

Keeping a process running and resuming work inside that process are separate outcomes. Version 1 provides application recovery. It does not automatically send prompts, click Resume, bypass sign-in, or guarantee that an interrupted job resumes. Actual overnight-job continuation must be verified with the initial target application.

This is a new C# product informed by Resurrector's functionality, rather than a continuation of its Go/Wails implementation. Product requirements below govern the new application; upstream behaviors are explicitly identified as reference findings.

## 2. Goals and scope

### Goals

1. Recover an unexpectedly closed application without user action, within the configured detection and retry intervals.
2. Prevent endless restart loops through a configurable recovery budget.
3. Detect short-lived starts using a configurable observation period and faster checks.
4. Continue looking for a manually restarted application after automatic recovery is suspended.
5. Rearm automatic recovery after that application demonstrates stability.
6. Provide a clear native Windows interface and a durable, searchable operational history.
7. Support multiple independent applications without coupling their failures or recovery budgets.
8. Preserve protection settings and failure state across Relight restarts and Windows sign-ins.

### Version 1 scope

Tray operation; application registration; attach to an existing instance; executable and supported installed-app launch methods; normal and observation polling; bounded recovery; manual-start discovery and rearming; persistent state; dashboard; configuration editor; logs and export; notifications; pause/resume; start at sign-in; basic diagnostics; portable Windows x64 distribution.

The initial release must work with the user's actual installed target. If it is packaged or launched through Windows activation, that launch adapter is a release requirement, not a deferred enhancement.

### Later scope

Weekly protection windows and scheduled actions; additional health checks; advanced shutdown commands; exponential backoff; opt-in recovery of Relight itself; import from Resurrector; ARM64 packaging. Section 15 defines these extensions.

### Non-goals for Version 1

Cross-platform support; running GUI applications before sign-in; a Windows service replacement; remote administration; account/password storage; automatic sign-in; sleep prevention; automatic Windows reboot; application repair or installation; arbitrary workflow automation; built-in ChatGPT/Codex task continuation; forcibly restarting an application merely because its window appears unresponsive.

## 3. Evidence and reference boundaries

The Resurrector review used the local checkout at `C:\dev\Resurrector`, commit `7a510223f0bfdf742d03cf6185343f78c0d18ef0`, with package version 1.2.0. Findings are based on source and documentation inspection, not a completed build or runtime evaluation. The requested compilation was stopped when the product direction changed.

Reference identifiers in this document point to the pinned upstream source:

| ID | Source | Reviewed subject |
|---|---|---|
| R1 | [README](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/README.md) | User-facing behavior, configuration and installation |
| R2 | [Architecture](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/doc/design.md) | Core/UI separation, reconciliation and desktop-session design |
| R3 | [Monitor](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/core/monitor.go) | Retry counter, stability threshold, failure state, launch and shutdown |
| R4 | [Reconciler](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/core/reconciler.go) | Apply settings, enable/disable, remove, hot reload |
| R5 | [Configuration](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/util/config.go) | Fields, validation, defaults and atomic saves |
| R6 | [Logger](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/util/logger.go) | Append logging, text/JSON formats |
| R7 | [UI](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/ui/frontend/src/App.svelte) | Grid, editor, context menu and restart action |
| R8 | [Tray](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/core/tray.go) / [startup registration](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/core/autostart.go) | Tray commands and current-user sign-in startup |
| R9 | [Main](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/core/main.go) / [IPC](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/core/ipc.go) | Singleton, bootstrap, reload and UI communication |
| R10 | [License](https://github.com/Maki-Daisuke/resurrector/blob/7a510223f0bfdf742d03cf6185343f78c0d18ef0/LICENSE) | MIT license; retain applicable notices if code/assets are reused |

### Functionality assessment and product disposition

| Resurrector capability or behavior | Evidence | Relight decision |
|---|---|---|
| Resident tray core with separately launched settings UI | R2, R8, R9 | Retain background operation independent of whether the dashboard is open; process separation remains an engineering choice. |
| Multiple named applications; command, arguments, working directory, hidden-window option | R1, R5 | Retain, using stable profile IDs and a native editor. |
| Browse/drop executable; editable table and context actions | R7 | Retain; add installed-app and running-process selection. |
| Launch and monitor owned child processes; Windows process-exit notifications | R3 | Add adoption of independently launched processes; use polling as the authoritative reconciliation fallback. |
| Delay between restarts and configurable retry limit | R3, R5 | Retain, with explicit attempt accounting and durable limits. |
| Healthy runtime checked when a process exits; can reset restart counter | R3 | Complete observation and reset the budget while the application is still running. |
| Retry exhaustion marks Failed and returns from the monitor loop | R3 | Continue passive discovery in Awaiting intervention. |
| Retry count initialized in memory when the loop starts | R3 | Persist recovery budget and lockout across monitor restarts. |
| Disabling/removing a profile stops its process; exiting stops all monitored processes | R3, R4, R8 | Pause, disable, remove and exit leave target applications running by default. |
| Job Objects use kill-on-close for owned process trees | R3 | Do not bind normal desktop-application lifetime to Relight's lifetime. |
| Monitoring settings reload without restarting the target; launch changes can restart it | R4 | Retain live policy updates; launch edits apply to the next launch unless Restart now is explicitly selected. |
| Atomic TOML saves and last-good behavior after invalid live edits | R5, R9 | Retain atomic validation and last-good behavior; proposed new format is versioned JSON. |
| GUI Restart writes disabled, waits, then writes enabled | R7 | Use an explicit acknowledged restart operation. |
| Text/JSON append logs, default stderr, Info-level logger | R6 | Add default persistent logs, rotation, searchable history, retention and diagnostic export. |
| Graceful close, optional shutdown command and forced fallback | R3 | Explicit manual stop/restart in Version 1; advanced stop-command configuration later. |
| Environment placeholders and argument parsing | R5 | Support environment expansion with validation and argument-safe launching. |
| Start at sign-in; one core instance per Windows session | R8, R9 | Retain; a second launch opens the existing dashboard. |
| First-run example command is enabled and starts a sample process | R9 | Start with no active profiles; setup explicitly enables protection. |
| No schedule or automatic adoption-after-lockout path found in reviewed core | R3, R4, R5 | Adoption is essential in Version 1; scheduling follows as a defined extension. |

## 4. Users and primary journeys

The primary user runs lengthy desktop jobs unattended and needs predictable recovery with enough evidence to diagnose repeated failures. A secondary user wants the same behavior for several utilities, developer tools or desktop applications.

### Journey A: First setup

Open Relight → Add application → select installed app, running process or executable → verify identity and launch method → review default recovery policy → Save and enable. If the target is running, adopt it. If absent, the default policy waits for the user's first start; the user can instead select Start now or Start automatically when absent.

The default wait-for-first-start behavior reflects monitoring work the user has chosen to begin. An explicit alternative supports applications that should always be present.

### Journey B: Overnight recovery

A previously stable application disappears. Relight confirms absence, logs an outage, waits the configured retry delay, and tries a launch. It watches the resulting application closely for ten minutes. A stable result restores normal monitoring and a fresh recovery budget.

### Journey C: Repeated failure and manual repair

Three automatic recovery attempts fail to reach stability. Relight stops launching, displays Awaiting intervention, notifies once, and continues passive detection. The user repairs the problem and launches the application normally. Relight detects it, observes it for ten minutes, and restores automatic protection without requiring a separate reset click.

### Journey D: Intentional maintenance

The user pauses protection before closing or updating an application. Relight leaves it alone. On resume, Relight reconciles current state and preserves any previous lockout; pausing does not silently reset a failure budget.

### Journey E: Morning investigation

The user opens History, filters to overnight events and the target application, and sees each outage, attempt, early exit, stable recovery and suspension reason. They can export the relevant records without copying unrelated application data.

## 5. Policy defaults and terminology

The following are proposed defaults, not additional user mandates. Settings are per application unless stated otherwise.

| Setting | Proposed default | Supported Version 1 range / meaning |
|---|---|---|
| Normal poll interval | 120 seconds | 60–300 seconds |
| Observation period | 10 minutes | 1–60 minutes; UI presets include 5 and 10 |
| Observation poll interval | 5 seconds | 1–30 seconds; must not exceed normal interval |
| Discovery interval during lockout | 30 seconds | 5–300 seconds |
| Maximum automatic recovery attempts | 3 | 0–20; zero means no automatic launches; unlimited retries excluded from Version 1 |
| Delay before each recovery attempt | 30 seconds | 5–3,600 seconds |
| Launch appearance timeout | 60 seconds | 5–300 seconds |
| Absence confirmation delay | 2 seconds | 1–10 seconds; excludes single transient lookup failures |
| Stop grace period | 15 seconds | 5–120 seconds; only for explicit stop/restart |
| On enable if target is absent | Wait for first start | Alternative: start automatically |
| Rearm after stable external start | Enabled | May be disabled for users requiring explicit reset |
| Target exits normally while protected | Recover | Applies to any confirmed disappearance; exit code alone does not reveal user intent |
| Relight starts at sign-in | Off until enabled | Current-user setting |
| Lockout and recovered notifications | On | Individual notification preferences |
| Logging | Information | Debug available temporarily |
| Log retention | 30 days, 250 MB total | Delete oldest first when either bound is exceeded; configurable |
| Rotation size | 10 MB per file | Configurable |

**Observation** means verified process presence throughout an uninterrupted interval of monitoring. It does not establish that a job is making progress or that a UI is responsive.

**Recovery episode** begins when an armed target is confirmed absent or an explicit start/restart fails. It ends when the target completes observation, or the user explicitly resets recovery. A later outage after successful observation starts a new episode.

**Automatic attempt** is one launch dispatch made by Relight's recovery engine. It includes a dispatch that fails immediately. Passive checks and externally initiated launches are not attempts.

**External start** means a matching process was discovered without being correlated to a Relight launch operation. The product may describe it as a detected manual restart for usability, but logs must not claim certainty about which person or program launched it.

## 6. Recovery state model

### Lifecycle states

| State | Meaning | Automatic launch allowed? |
|---|---|---|
| Disabled | Profile explicitly disabled | No |
| Waiting for first start | Enabled but not yet armed; target absent | No |
| Starting | One launch operation is waiting for the real application to appear | No second launch |
| Observing | Target is present; stability timer is running | No |
| Healthy | Target passed observation; normal monitoring | Only after confirmed disappearance |
| Retry waiting | Confirmed outage; attempt scheduled | At deadline, subject to policy and fresh absence check |
| Awaiting intervention | Recovery budget exhausted or explicit action required | No; discovery continues |

Two overlays avoid an unnecessarily large state machine: **Paused** suspends policy actions, and **Detection unavailable** records an unreliable/ambiguous process lookup. The dashboard prioritizes these overlays while preserving the underlying lifecycle state and budget. A pause can apply to one application or globally.

Observing carries a provenance flag: initial adoption, automatic launch, explicit user launch, or external start during lockout. The last category retains the lockout until stability is proven.

### Required transitions

1. Enabling a new profile first discovers existing instances. A uniquely identified target enters Observing. If absent, use the selected initial-start policy.
2. Healthy → confirmed absence → Retry waiting, or Awaiting intervention if no budget remains.
3. Retry waiting → perform a fresh discovery check → adopt a matching process if present; otherwise persist attempt reservation and enter Starting before launch dispatch.
4. Starting → target appears before timeout → Observing. The launcher exiting is not itself proof of failure when an adapter expects handoff.
5. Starting → dispatch error or timeout → mark attempt failed; Retry waiting if budget remains, otherwise Awaiting intervention.
6. Observing → same logical target remains verifiably present for the full observation period → Healthy; reset attempt count and close the episode.
7. Observing after an automatic launch → early exit → mark that attempt failed; retry or lock out. Do not charge another attempt until another launch is dispatched.
8. Awaiting intervention → external target discovered → Observing with lockout retained. Stability → Healthy and automatic rearm if enabled. Early exit → Awaiting intervention without an automatic launch.
9. If automatic rearm is disabled, a stable external target is shown as running with recovery still suspended until Reset recovery is selected.
10. External starts before retry dispatch cancel that dispatch and enter Observing. If they exit early, retain the existing budget and retry eligibility; an external start during a persisted lockout never restores eligibility by itself.
11. Disable, pause, remove or quit cancels pending launch dispatches and leaves target processes running. Already-dispatched starts are reconciled and reported, not undone by killing a process.
12. Unknown identity, access-denied lookup, ambiguous instances or enumeration failure → Detection unavailable. Do not interpret unknown as absent. Recover the previous state when reliable detection returns; observation restarts from zero if continuity was lost.

### Exact attempt accounting

The disappearance of an existing application does not consume an attempt. With a limit of three, Relight may dispatch automatic launch 1, launch 2 and launch 3. If each fails to reach stability, it must never dispatch launch 4 in that episode.

Launching attempt 3 does not immediately imply lockout: it is allowed its full appearance timeout and observation period. If it succeeds, the budget resets. If it fails, lockout begins.

Record an attempt reservation durably before dispatch. A Relight crash between reservation and dispatch conservatively consumes the reserved attempt; show Interrupted attempt in history. This prioritizes the user's upper bound over guessing whether an unrecorded launch occurred.

An explicit Start now/Restart now is a user operation, recorded separately from automatic attempts. If it fails, subsequent automatic recovery can use the remaining budget unless already locked out. Start now while locked out allows one user-authorized start but does not clear lockout until stability. Reset recovery explicitly creates a fresh budget and reconciles the target before any automatic dispatch.

Attempt budgets survive pause/resume, disable/enable, application rename, Relight restart and Windows restart. Ordinary policy edits do not clear lockout; explicit reset or a qualifying stable start does. Increasing a limit alone does not release a locked profile. Reducing a limit never stops a running target; it limits subsequent launches.

### Worked example

| Time | Event | State / accounting |
|---|---|---|
| 01:00 | Previously healthy app closes | Outage begins; 0/3 automatic attempts |
| 01:02 | Normal poll detects absence; second check confirms | Retry waiting |
| 01:02:32 | Automatic launch 1 | 1/3; Starting then Observing |
| 01:03 | App exits before ten minutes | Attempt 1 failed |
| Later | Launches 2 and 3 also fail observation | 3/3; Awaiting intervention |
| 08:00 | User repairs and launches app | Discovered within lockout polling interval |
| About 08:00:30 | Matching app adopted | Observing; 3/3 retained, no automatic launches |
| About 08:10:30 | Ten observed minutes complete | Healthy; 0/3; protection rearmed |

If the external start exits at 08:04, Relight returns to Awaiting intervention. It does not begin three more launches.

## 7. Functional requirements

All requirements in this section are Version 1 unless explicitly marked Later.

### Application registration and identity

**APP-01:** Create, edit, duplicate, disable and remove application profiles. Each profile has an immutable ID and editable display name. Renaming preserves state and history. Duplicating creates a new disabled profile.

**APP-02:** Register from a running-process picker, executable browser, dropped executable, or installed-app picker. Display resolved identity, launch method and current detection result before enabling protection.

**APP-03:** Separate launch identity from running-process identity. Support absolute executable path plus arguments/working directory, and a Windows installed-app activation adapter when required by the initial target. Shortcuts or launchers must resolve to a validated target identity; their temporary PID is not the application identity.

**APP-04:** Match within the current user's interactive session. Prefer canonical executable path or package/application identity. Track PID plus process start time to guard against PID reuse. Name-only matching is an explicitly labeled fallback, never an automatic default.

**APP-05:** For multiple instances with the same executable, allow an additional argument-based discriminator when readable. If matching remains ambiguous, show candidates and suspend automatic launches. Never kill supposed duplicates to force a match. Detect overlapping enabled profiles and block conflicting control of one logical target.

**APP-06:** Treat main process and helpers as a logical application using the configured adapter. Surviving helper processes must not conceal the main application's absence. A main window is not required when a valid tray/background instance remains alive.

**APP-07:** Resolve packaged application identity at launch so installation-version paths are not assumed permanent. If an update invalidates identification, enter a diagnosable unavailable state and require re-selection when resolution cannot recover.

### Monitoring and recovery

**MON-01:** Provide independently configurable normal, observation and lockout-discovery intervals. Use process-exit events opportunistically; periodic reconciliation remains required for discovery and missed events.

**MON-02:** Reconfirm absence before starting recovery and recheck immediately before dispatch. Serialize events and commands for each profile so polls, exit events and UI actions cannot double-launch.

**MON-03:** Enforce the state and accounting rules in Section 6 with cancellable timers and one outstanding launch per profile. A global launch queue should spread simultaneous recoveries without changing individual attempt limits.

**MON-04:** Observation begins when the real target is positively detected. A new process instance or unverified handoff restarts observation. A verified adapter handoff may preserve logical identity only under a documented adapter rule.

**MON-05:** On lockout, retain passive detection and deliver one intervention notification per episode. Notification display must never block the monitoring engine.

**MON-06:** Rearm after a qualifying external start and record a distinct Recovery rearmed event. Mere appearance must not reset the failure count.

**MON-07:** Default exit policy recovers all confirmed disappearances while armed, including normal exits. Relight cannot reliably infer whether a close was intentional. Provide Pause and Stop and pause for intentional shutdown.

**MON-08:** On resume from sleep or session unlock, immediately reconcile; do not replay accumulated missed polls or dispatch a burst of retries. Sleep and periods without reliable monitoring do not count toward observation. Restart observation after such a gap; preserve lockout and budget.

**MON-09:** When Relight restarts, load persisted state, then discover before launching. A locked-out profile stays locked even if absent. A present application enters observation with the prior lockout/budget retained. Unobserved downtime does not establish stability.

**MON-10:** If process ownership or privileges prevent reliable monitoring, show actionable diagnostics. Version 1 must not repeatedly raise UAC prompts or attempt to bypass elevation requirements.

### User controls and configuration

**CTL-01:** Provide Start now, Restart now, Pause protection, Resume protection, Stop and pause, Reset recovery, Edit, Remove and View history. Actions must have distinct meanings and be enabled only when applicable.

**CTL-02:** Pause/disable/remove/quit leave applications running. Explicit Restart now and Stop and pause may terminate only the selected, positively identified target. Attempt graceful close first. If it does not exit, show the user a force-close choice rather than silently killing a job that may be saving work.

**CTL-03:** Reset recovery records a user action and clears the episode budget. If the target is absent and auto-recovery is enabled, queue a launch after the normal recovery delay; show this consequence in the action label/help. If present, observe without launching a duplicate.

**CTL-04:** Validate numeric ranges, executable/activation identity, arguments and directories before saving. Missing targets encountered later produce diagnostics rather than silently deleting profiles.

**CTL-05:** Changes to monitoring intervals apply live. A changed observation duration restarts the current observation timer. Retry-delay changes recompute a pending deadline from the save time. In-flight launch operations retain their original timeout/policy snapshot. Launch identity changes cancel pending dispatches and trigger fresh discovery without stopping the old application; the editor explains that the old target is no longer protected after an identity change.

**CTL-06:** Save configuration atomically with schema version, revision and last-good backup. Reject stale concurrent writes. Invalid external edits retain the last-good configuration and display a non-blocking error. Initial invalid configuration opens a repair screen with automatic launches suspended.

**CTL-07:** Use current-user settings and sign-in startup, with no administrator requirement for ordinary same-user targets. Second Relight launch activates the current dashboard. Recreate the tray icon after Windows Explorer restarts.

## 8. Interface requirements

### Tray

Tooltip summarizes protected, observing, paused and intervention-needed applications. Icon priority: intervention/detection problem, then observation/retry, then healthy, then all paused/disabled. Labels accompany colors in the dashboard. Menu: Open dashboard, Add application, Pause all, Resume all, View history, Start at sign-in, Exit Relight. Exiting explains that protection stops and applications remain running; the user may suppress that informational prompt.

### Dashboard

The main **Monitoring** page provides a compact read-only grid with one row per configured application, including paused and disabled profiles. Show status, the last verified PID, process age at its last verified check, last-check age, recorded automatic relaunch dispatches in the rolling last 24 hours, and the current episode attempt budget. Process age is not measured availability or uninterrupted observation. Unknown process information must remain unknown. Relaunch counts exclude manual and external starts and do not imply successful recovery; incomplete or unavailable history must be labeled. Keep setup and explicit controls on the **Applications** page. Monitoring is the initial page and the tray's Open dashboard destination; existing Ctrl+1–3 shortcuts remain, with Ctrl+4 added for Monitoring.

Sortable/filterable table with application name/icon, status, PID or logical-instance summary, attempt count, last seen, last outage, last successful recovery and next action. Display countdowns for launch timeout, retry delay and observation. A selected row shows a concise explanation such as “Automatic recovery suspended after 3 unsuccessful attempts. Still watching for a restart.”

Primary actions appear on the row or details panel and in the context menu. Closing the dashboard keeps monitoring active. Empty state offers Add application and explains what Relight can recover.

### Application editor

Sections: Identity and launch; Monitoring; Recovery; Notifications. Show readable units rather than requiring conversion to seconds. Include policy preview: “Check every 2 minutes. After starting, observe every 5 seconds for 10 minutes. Try recovery up to 3 times, then wait for intervention.”

Detect now is read-only. Test launch is an explicit action, first checks for an existing match, and reports the real target discovered. Save alone must not terminate an application. Saving an enabled profile with Start automatically selected clearly states that an absent target will be launched.

### History and settings

History offers application, severity, event type, episode and time-range filters; local-time display with UTC available; expandable diagnostics; copy details; open log folder; export selection. It must support an overnight summary of outages, attempts, successful recoveries, lockouts and observed unavailable time. Monitoring gaps must be labeled unknown, not counted as confirmed application downtime.

Global settings include startup, notification preferences, retention, theme and diagnostics. Support keyboard navigation, accessible labels, screen readers, light/dark/high-contrast themes and 100–200% display scaling. Do not rely on color alone to signal failure.

## 9. Logging and diagnostics

**LOG-01:** Persistent structured JSON Lines logs are on by default. The UI provides readable presentation and plain-text/CSV export. Configuration lives under `%LOCALAPPDATA%\Relight`; logs are stored in its `Logs` subfolder.

**LOG-02:** Every state transition, launch reservation/dispatch/result, timeout, early exit, observation completion, detected external start, lockout, rearm, manual action, policy change, startup/shutdown and monitoring gap receives an event. Routine successful polls are Debug-level; aggregate counters prevent verbose overnight logs by default.

**LOG-03:** Records include UTC timestamp, event ID, severity, message, profile ID/display name, episode ID, operation ID, prior/new state, process identity when known, launch origin, attempt number/limit, relevant durations, reason code and OS error/exit code when available. Unknown values remain unknown; absence alone must not be labeled a proven crash.

**LOG-04:** Rotate at the configured size and enforce age plus total-size retention. Log cleanup must not remove configuration, runtime state or arbitrary user files. Exported bundles are outside automatic log retention.

**LOG-05:** Redact sensitive argument fields and avoid recording full environment contents, credentials, application window contents or chat text. Diagnostic export includes redacted configuration, recent events, application version, OS details and adapter diagnostics. The user chooses the destination; export does not upload anything.

**LOG-06:** Logging failures produce a persistent degraded-status banner and bounded in-memory event buffer. If durable recovery-state writes also fail, suspend new automatic launches to avoid losing the attempt cap. Existing applications and passive detection continue.

**LOG-07:** Launch errors must distinguish invalid configuration, missing target, activation failure, permission failure, timeout and early exit. Preserve useful native error codes and actionable remediation text.

## 10. Persistence and data model

Keep policy, live state and event history separate. Proposed storage is versioned JSON for policy and atomic state snapshots, with JSON Lines event files. An indexed history store can be introduced if profiling requires it.

| Record | Required fields |
|---|---|
| Application profile | ID, name, enabled flag, launch adapter/data, match adapter/data, arguments, working directory, initial-start policy, monitoring intervals, observation duration, retry limit/delay, appearance timeout, auto-rearm preference, notification settings |
| Runtime state | Profile ID, session identifier, underlying state, pause flags, armed flag, episode ID, reserved attempt count, lockout/reason, last verified target identity, observation metadata, pending operation/deadline, configuration revision |
| Recovery episode | ID, profile ID, start/end time, cause, attempts, result, external-start observations, known monitoring gaps |
| Operational event | Fields in LOG-03 |
| Global settings | Schema version, startup preference, retention, theme, notification preferences |

State writes must be atomic and durable at launch reservation and lockout boundaries. Corrupted state must not silently imply a fresh budget: retain evidence, show recovery-state unavailable, allow passive discovery, and require explicit reset before automatic launching for affected profiles. First-run absence of a state file is valid only when initializing a genuinely new profile.

Persist UTC for history and reconstruction. Use monotonic elapsed time for in-session delays so system-clock changes do not shorten observation or multiply retries. Store enough metadata to detect discontinuities after restart. Migration failures preserve original files and keep automatic actions suspended rather than overwriting data.

Multiple signed-in sessions must not write the same live-state files concurrently. Use session-scoped state and coordinated/revision-checked shared configuration. A lockout for the user's profile carries across sign-ins; stale PID/session data must never authorize killing a process in a different session.

## 11. Proposed C# architecture

These are implementation recommendations to validate during design, not claims that the application is already built.

- **Native shell:** WPF with MVVM, a Windows notification-area integration and an application lifetime independent of dashboard windows. Choose a supported .NET LTS release when implementation begins.
- **Domain engine:** UI-independent C# state machine and policy evaluator. Inject clock, persistence, process discovery and launch interfaces for deterministic testing.
- **Windows adapters:** Win32 executable launch/discovery and installed-app activation/discovery. Add adapters without changing retry policy. Explicitly model launcher handoff and multi-process identity.
- **Per-profile coordinator:** Serialized command/event handling, cancellation and operation IDs. A shared scheduler avoids one polling thread per profile.
- **Storage/logging:** Versioned configuration, durable state repository, structured rolling logs, asynchronous history loading and export.
- **Notifications:** Separate, non-blocking delivery. Notification or dashboard failures must not stall recovery coordination.

Prefer one native executable initially if it meets reliability requirements; keep engine and UI code separable. Do not reproduce Resurrector's assumption that monitored applications die with the supervisor. A watchdog crash or upgrade must leave desktop targets running. A later separate engine process can strengthen isolation if testing justifies it.

Produce a self-contained Windows x64 package with version metadata, documented build commands and dependency licenses. No automatic updates during protected overnight work in Version 1.

## 12. Reliability, performance and operational constraints

| Area | Acceptance target |
|---|---|
| Detection | Confirm absence within normal poll interval + configured confirmation delay + 5 seconds of scheduling allowance on an awake, normally loaded machine |
| Early failure | Detect within observation poll interval + 5 seconds, or sooner via a reliable exit event |
| External-start discovery | Within lockout discovery interval + 5 seconds |
| Observation completion | Healthy/rearmed within observation interval + one observation poll + 5 seconds |
| Scale | 50 configured profiles with independent state and recovery budgets |
| Idle CPU | Target below 1% total CPU averaged over 15 minutes with 20 ordinary profiles; measure on a documented reference PC |
| Resident memory | Initial target below 150 MB working set with dashboard closed; report engine/UI footprint during profiling |
| UI responsiveness | Normal actions acknowledge within 250 ms; long operations expose progress and cancellation where applicable |
| Reliability | Pass a 24-hour soak with repeated target failures, UI open/close cycles and no unbounded memory/log growth |
| Session lock | Monitoring continues while the user session is locked and the machine remains awake |
| Sleep/logoff | No promise of work during sleep or after logoff; resume/sign-in behavior follows persisted policy |
| Isolation | One target's failure or blocked launch cannot stall other profiles |
| Data | Restart cannot erase a lockout or exceed its persisted recovery budget |

Timing targets exclude OS suspension and severe system resource starvation; those gaps must be visible in diagnostics. Test measurements must state machine configuration and monitored-profile count.

## 13. Acceptance scenarios

Use a small controllable test application that can remain running, exit after a delay, spawn helpers and hand off to another process. Do not test destructive restart behavior against a real overnight job.

| ID | Scenario | Required result |
|---|---|---|
| AT-01 | Enable profile while target already runs | Adopt without duplicate launch; enter observation then Healthy. |
| AT-02 | Enable absent profile with wait-for-first-start policy | No launch; first external start is detected and monitored. |
| AT-03 | Stable application exits | Confirm absence, wait delay, dispatch one recovery; event chain is complete. |
| AT-04 | Limit 3; every automatic start fails immediately | Exactly three automatic dispatches; no fourth; lockout persists. |
| AT-05 | Attempt 3 starts and survives observation | Continue watching attempt 3; become Healthy and reset budget. |
| AT-06 | Attempts exit late in the observation period | Each remains an unsuccessful attempt; no early reset. |
| AT-07 | External start after lockout stays stable | Discover, observe, clear lockout and rearm without UI reset. |
| AT-08 | External start after lockout exits early | Return to lockout; no automatic launch and no budget reset. |
| AT-09 | User starts target during retry countdown | Cancel pending recovery and adopt existing instance. |
| AT-10 | Concurrent poll, exit callback and UI action | At most one launch operation; consistent logged operation IDs. |
| AT-11 | Restart Relight during lockout or reserved launch | Preserve budget; discover before launching; interrupted reservation is not refunded silently. |
| AT-12 | Pause/disable/remove/exit while target runs | Target remains alive; no queued automatic dispatch occurs afterward. |
| AT-13 | Explicit Stop and pause / Restart now | Operate only on verified target; graceful timeout requires force-close choice. |
| AT-14 | Rename or change policy | History remains attached; no silent budget reset or target termination. |
| AT-15 | Broken external config edit | Last-good configuration remains active; visible non-blocking diagnostic. |
| AT-16 | Target installed-app path changes after update | Resolve stable identity or report unavailable; no restart storm. |
| AT-17 | Launcher exits but real target runs | Successful target discovery; launcher exit is not counted as target failure. |
| AT-18 | Main process dies while helper survives | Detect logical target absence according to adapter; helper does not mask failure. |
| AT-19 | Multiple matching instances or access denied | Show ambiguity/unavailable; do not kill or launch on uncertain evidence. |
| AT-20 | Sleep/resume or system-clock change | No retry burst, no false stability from sleep, correct subsequent timing. |
| AT-21 | PID reused by unrelated process | Do not adopt or stop it based on PID alone. |
| AT-22 | Restart Explorer / launch second Relight instance | Tray recovers; second instance opens existing UI; no duplicate engine. |
| AT-23 | Disk full / corrupted runtime state | Diagnostics visible; stop automatic launches if durable budget cannot be trusted; keep targets alive. |
| AT-24 | Multiple applications fail together | Independent counters; serialized launches per profile; UI remains usable. |
| AT-25 | Log retention and export | Retention bounds hold; selected episode export is complete and redacted. |
| AT-26 | Actual installed ChatGPT target closes | Relaunch succeeds, real app identity is detected, observation succeeds; job continuation is reported separately. |
| AT-27 | Automatic recovery limit is zero | No automatic launches; detection and explicit starts still work. |
| AT-28 | Automatic rearm disabled | Stable external start is visible but does not release lockout until explicit reset. |
| AT-29 | Force Relight itself to exit unexpectedly | Target applications remain alive; next Relight startup restores policy/state. |
| AT-30 | Invalid state/config migration | Original files preserved; no silent fresh budget or unsolicited launch. |

Unit tests cover state transitions, boundary timing, attempt reservations and policy edits using a fake clock. Windows integration tests cover process identity, launch adapters, session isolation, persistence and tray behavior. End-to-end acceptance covers dashboard workflows and the user's real target, with no active valuable job during forced-exit tests.

## 14. Delivery plan and release gates

### Milestone 0 — Validate target and settle product choices

Identify whether the intended application is ChatGPT desktop, Codex desktop or both; record installed identity and launch mechanism. Verify whether relaunching resumes a representative interrupted job. Select .NET baseline and confirm proposed defaults. Prototype discovery/activation for the actual installation before building a large UI.

**Exit:** A documented target adapter contract and explicit statement of application-recovery versus job-resumption behavior.

### Milestone 1 — Recovery engine

Implement profiles, discovery, state machine, observation, bounded recovery, external-start adoption, durable state and structured events. Build the controllable test target and deterministic policy tests.

**Exit:** AT-01 through AT-11, AT-17 through AT-21, AT-23, AT-24 and AT-27 through AT-30 pass at engine/integration level.

### Milestone 2 — Native tray application

Implement dashboard, editor, controls, history, notifications, startup integration, configuration validation and diagnostics export. Connect actual target adapter.

**Exit:** Remaining acceptance scenarios pass; accessibility and first-run flow reviewed.

### Milestone 3 — Overnight pilot and distribution

Run the 24-hour soak and supervised overnight pilot; validate retention, CPU/memory targets, locked-session behavior, recovery after update and package installation/removal. Publish a reproducible build recipe and a local release package for testing.

**Exit:** All Version 1 acceptance tests pass, no unresolved duplicate-launch or budget-loss defects, and the user can diagnose the complete overnight episode history.

No delivery dates are committed before the target-adapter spike. That integration is the largest unknown; basic tray controls are not the main risk.

## 15. Planned extensions

Storage protection is an explicitly authorized extension (18 October 2026). Section 18 defines its staged scope; increment S1 is authorized alongside the existing recovery work. It does not change recovery budgets or existing release acceptance gates.


| Priority | Feature | Defined behavior / boundary |
|---|---|---|
| Next | Weekly protection windows | Select days, local time range and time zone; support overnight ranges. Outside the window, leave target running and suspend recovery. On reentry, reconcile; do not clear lockout. |
| Next | Scheduled Start / Stop and pause / Restart | Explicit scheduled actions with visible consequences; no raw cron syntax required. A start window alone does not imply permission to stop a running target. |
| Next | Schedule edge cases | Specify DST handling, timezone changes and missed-run policy. Default missed actions are skipped after sleep; no catch-up burst. |
| Next | Configurable retry backoff | Fixed, linear or exponential delays, bounded by a maximum; same durable attempt semantics. |
| Next | Optional health probes | Window responsiveness, heartbeat, local HTTP or user-supplied probe. Each needs startup grace, timeout and false-positive policy before it can trigger termination. |
| Next | Advanced shutdown | Optional stop executable/argument list and timeout; success determined by target exit, with ownership rules for child trees. |
| Later | Supervisor self-recovery | Opt-in scheduled check or separate supervisor with explicit user-exit suppression and its own restart cap. It must not prevent intentionally quitting Relight. |
| Later | Resurrector import | Read TOML, preview translations and unsupported settings, create disabled profiles; never silently inherit unlimited retries or kill-on-exit behavior. |
| Later | Additional integrations | ARM64, CLI control, application-specific progress adapters and optional external notifications; separate requirements and authorization. |

## 16. Open decisions and assumptions

These do not prevent reviewing this PRD. Proposed defaults allow implementation planning without treating unanswered preferences as approval for unrelated behavior.

| Decision | Current proposal / assumption | When it must be settled |
|---|---|---|
| Product name | Relight; repository currently named CodexKeepAlive | Confirmed by the user |
| Actual first application | Confirmed: ChatGPT (`OpenAI.Codex_2p2nqsd0c76g0`) only. ChatGPT Classic (`OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0`) is a distinct unsupported application. The package family's `Codex` text does not make Codex a second target. | Target selected; absent-instance recovery still needs M0/M4 validation |
| Default first-start policy | Wait for user's first launch, then recover automatically | Before first-run UX is finalized |
| Stable manual-start requirement | Ten-minute observation before clearing lockout | Before engine acceptance is finalized |
| Protection schedules | Follow Version 1 rather than delaying the core watchdog | Before implementation scope approval |
| Windows support | Windows 11 x64 initially; other Windows versions require explicit testing | Milestone 0 |
| UI framework | WPF/MVVM; supported .NET LTS chosen at implementation | Technical design |
| Handling hangs | Detection-only diagnostics in Version 1; no automatic forced termination | Before health-probe extension |
| Job continuation | Not assumed; verify on the real application | Milestone 0 and pilot |
| Source/assets reuse | Prefer fresh C# implementation; retain applicable MIT notices if reusing upstream material | Before release |

## 17. Success definition

Version 1 is successful when the user's selected desktop application is recovered after an unexpected exit, unstable starts never exceed the configured automatic attempt budget, a stable manual restart restores protection automatically, and the morning history explains what happened without guesswork. These properties must continue to hold across Relight restarts, session interruptions and ordinary application updates.


## 18. Storage protection

**Scope decision — 18 October 2026:** Add configurable disk-space monitoring and local notifications now (S1). Follow with offender analysis (S2), reviewed cleanup and reusable rules (S3), then explicitly opted-in automatic cleanup (S4). Application recovery remains independent. No deletion or folder scan is authorized by enabling monitoring.

### S1 — Drive monitoring and notifications

- **DS-01:** Monitor fixed local drives while resident, independently of dashboard visibility and recovery scheduling. Default to the Windows system drive; users can explicitly select additional drive-letter roots. Automatic selection of application data drives, removable/network drives and mounted-folder volumes is deferred.
- **DS-02:** Show available space to the current user in GiB and percent, severity, last check time and an explicit unavailable result. An unreadable drive is never treated as empty or healthy. One failed reading must not hide other results. Use native capacity sampling, not directory enumeration.
- **DS-03:** Persist enablement, notification preference, check interval (10–3600 seconds; default 60), and per-drive warning/critical percent and GiB thresholds. Defaults: warning below 10% OR 20 GiB; critical below 5% OR 5 GiB. Both threshold dimensions apply; critical must be positive and strictly below warning. Warning percentage is at most 95. Empty configured drive list means the system-drive default.
- **DS-04:** Notify on entry into warning and escalation to critical, once per severity in the current alert episode by default. S6 optionally adds reminders and explicit snooze/resume reevaluation. A repeat or unavailable reading does not otherwise rearm notifications. Clear the episode only when both warning thresholds are exceeded by at least 2 percentage points and 1 GiB. An alert retains critical severity until above both critical thresholds plus the same margins (bounded by warning margins). Notification failure is visible and independent of recovery. Windows can suppress delivery. Restarting Relight, re-enabling monitoring or changing a drive's thresholds starts a fresh evaluation; notifications are not durable delivery receipts.
- **DS-05:** Show storage attention through the tray and latest readings in Storage / Overview, with preferences in Monitoring settings. Turning off notifications leaves sampling active; disabling storage monitoring does not pause application recovery. Save through the existing atomic, last-good, revision-checked configuration store. Invalid saves preserve active settings. No elevation or new runtime dependency is required.
- **DS-06:** Run samples off the WPF dispatcher on a separate schedule. Missed intervals after sleep do not cause catch-up bursts. Keep only the latest readings and bounded per-drive episode state in memory. S6 adds reminder cadence and session snoozing; trend history and predicted exhaustion remain deferred.

### S2 — Offender analysis

**Authorized implementation — 18 October 2026:** On-demand analysis in the existing Storage window. This does not enable cleanup or scheduled scans.

- **DS-07:** Explicitly select a folder or drive on a fixed local drive, then start a cancellable metadata-only scan off the dispatcher. Permit one scan worker per Relight process across navigation and dashboard hiding/reopening. Under S7, scans continue across navigation/hiding; Cancel scan or exiting cancels traversal. Hiding the dashboard does not stop resident monitoring. Cancellation is checked between native operations; a blocked native operation may delay it. Once a completed scan begins its bounded atomic save, allow that save to finish.
- **DS-08:** Rank largest files and recursive folders by allocated bytes, showing logical size alongside allocated size. Logical totals count paths; allocated totals charge each volume/file identity once to its first encountered path. Folder rows overlap because parents include descendants; hard-link placement therefore affects which folder is charged. Query compressed/sparse allocation separately. These are observed primary file-stream sizes, not total volume usage or promised reclaimable capacity. Exclude directory overhead and alternate streams.
- **DS-09:** Never open file contents. Skip reparse points, offline and recall-on-access entries, including a selected root beneath such an ancestor. Do not hydrate cloud content or traverse junctions/mounted-folder volumes. Report inaccessible/changed files and interrupted enumerations explicitly, preserving already measured sizes as partial. Mark each folder's coverage and show scan root, timestamps and inspected/measured/skipped/alias counts. Live-tree observations are not point-in-time snapshots.
- **DS-10:** Default each scan to 100,000 inspected entries, configurable under S8 from 1 to 10,000,000, and depth 128. Retain the largest 100 files, 2,000 folders and first 100 coverage issues; display at most 100 rows per result category. Keep one latest local baseline (at most 4 MiB) plus one bounded staging file under `%LOCALAPPDATA%\Relight\Storage`. Use an exclusive writer lease, flushed staging write and atomic replacement. Preserve corrupt/unsupported baselines and report persistence failures while still displaying the current scan. Full scanned paths remain local and outside diagnostic bundles.
- **DS-11:** Compare positive logical-size growth only for the same root and retained paths with complete measurements in both scans. Missing/unmeasured paths have unknown growth, never assumed zero or deleted. Show baseline timestamp and limitations. Scanning a different root replaces the single baseline; canceled traversal does not replace it. Reopening loads the latest saved results without automatically scanning. No durable multi-root history or predictive forecast is included.

### S3 — Reviewed cleanup

Rules apply to one exact local folder's immediate files. Configure filename pattern/exclusions, minimum age (1–3650 days), newest-file retention, maximum 1–200 files and at most 10 GiB logical bytes per run. Supported extensions initially: .log, .tmp, .bak, .old, .dmp, .cache and .etl. Up to 50 rules use versioned, revision-checked atomic storage and a backup. No directory removal or recursive cleanup.

Previews inspect at most 10,000 entries, retain the first 100 issues, and show file identity, sizes, modification time and estimated allocation. Incomplete/unknown coverage blocks execution; individual selections default off. Edits invalidate preview; authority is single-use and expires after ten monotonic minutes. Confirm permanent deletion separately with Cancel as default. Reverify metadata/identity on the exclusive native DELETE handle used for disposition, while briefly guarding canonical ancestors. Skip changed, locked, hard-linked, hidden, system, read-only, encrypted and cloud/reparse files. Block protected system/application/repository/Relight locations, including ancestors of protected data. Optional executable guards must report absent in the current user's session; they cannot prevent later starts.

Durable pending evidence is required before each dispatch. Audit failure stops remaining files; interrupted pending results remain unknown and are never resumed automatically. Keep one latest run, at most 200 outcomes/2 MiB, outside recovery history and diagnostic exports. Show observed available-space change separately from allocation estimates. Cancel current work or exiting cancels between native operations; already-dispatched deletion cannot be undone. Provide Windows Storage settings for Windows-managed cleanup rather than claiming Recycle Bin moves reclaim capacity.

### Cleanup scope overview

- **S3 — Reviewed cleanup:** Explain candidate location, reason, age, estimated reclaim and reversibility. Preview and explicitly select actions. Offer Windows Storage settings for OS-managed cleanup. Capture exact-root rules with file filters, exclusions, age, retention and application-running constraints. Recheck candidates immediately before action. Report actual free-space change separately from file-size estimates. Recycle Bin moves on the same volume do not count as reclaimed capacity.
- **S4 — Opt-in automation:** Promote individually approved rules from report-only to reviewed or automatic execution. Never infer deletion authority from prior manual actions. Bound each run, skip changed/locked files, protect Relight configuration/recovery state, prevent reparse-point escapes, provide a disable switch and bounded local audit history. Specify permanent deletion explicitly. Add dedicated destructive-operation acceptance before enabling this stage.

### S4 — Individually approved automatic cleanup

The subsequent request to build the next part authorizes opt-in automation implementation. It does not enable any rule by default. A saved rule and a fresh complete preview are required before a separate, default-Cancel confirmation approves future matching files. Approval binds a unique consent ID to the exact saved-rule fingerprint, a 1–168 hour minimum interval (default 24) and a below-free-space trigger of 1–1024 GiB (default 20). Current preview file checkboxes do not constrain future approved matches; the confirmation must say so explicitly.

The global preference defaults off and only the approval action turns it on. Saving/removing any rule clears all approvals and returns global execution to off. Disable all cancels pending work locally before persisting off, and rejects future native dispatches after acknowledgement. An already dispatched delete cannot be undone. If persistence fails, show the failure and retain the local stop; do not claim the saved preference changed. Reapproval is needed to resume locally stopped execution.

A separate resident minute check runs off the dispatcher and recovery scheduler, shares the storage work gate, and dispatches at most one bounded run per pulse. Unknown capacity is not a trigger. Require both the durable UTC next-eligible deadline and a full monotonic interval in the current worker. One exclusive per-data-directory worker lease prevents concurrent sign-in owners; takeover/restart starts a full interval again. Reserve the next deadline durably before preview/dispatch; empty, failed or interrupted runs consume that interval. No catch-up burst or interrupted-run replay. Invalid application configuration prevents new automatic checks; invalid cleanup configuration blocks native dispatch. In-flight cleanup retains its rule snapshot and continues to require live cleanup authorization.

Each run obtains a new bounded preview, preserves S3 exclusions and same-handle verification, and rechecks age/newest-file retention before every file. Per-file authorization is serialized with approval changes; revoked approval stops remaining work. Keep the latest detailed run plus 100 bounded summaries, with reviewed/automatic origin and unknown pending outcomes. Show status/failures in Settings and notify for automatic outcomes; notification failure must not affect accounting or recovery.

### S5 — Cleanup history and run details

**Authorized implementation — 18 October 2026:** Add read-only cleanup history (embedded in Storage under S7). Show up to 100 summaries, newest first, with reviewed/automatic origin, local start time, deleted/skipped/uncompleted counts, finality and observed free-space change. Filter by origin or problems/skips/unfinished runs. Refresh explicitly to see new resident runs.

Select a run to see its ID, rule ID, local and UTC timestamps, recorded rule settings at execution, before/after available bytes, problem and per-file path/outcome/reason. Historical rule settings are descriptive evidence, never execution authority. Missing final outcomes mean in progress or interrupted, with pending results unknown. Do not infer success from a missing error, promise undo, or replay a historical operation. Free-space change remains an observation affected by other activity.

Retain a schema-versioned detail archive for at most 100 runs and 4 MiB in addition to the bounded latest-run and summary files. Archive final outcomes and preserve an unfinished latest run before a new run replaces it; update pending per-file evidence in the latest-run file. Evict whole oldest archived details under the byte cap, retaining summaries independently. Legacy/evicted details and older runs without rule snapshots must be labeled unavailable rather than reconstructed from current rules. Preserve corrupt/unsupported evidence and surface read/write failures; untrustworthy accounting continues to block cleanup. Keep paths local and outside diagnostic exports. Merely opening, filtering or refreshing history must not create rules, approve cleanup or delete files.

### S6 — Low-space reminders and notification snooze

**Authorized implementation — 18 October 2026:** Add a persisted global per-drive reminder cadence: 0 minutes (default, reminders off) or 15–10080 minutes. Initial warning and critical escalation still notify once per severity/episode without waiting for a reminder interval. Optional reminders use the latest known low-space reading and a monotonic interval since the last generated notice; reminders do not reset recovery or grant cleanup authority. Healthy recovery clears the episode using existing hysteresis. Unknown or muted samples never notify or rearm severity and postpone an existing reminder timer. A low-space entry first observed while muted can notify when notifications are enabled again. Missed intervals produce at most one notice per drive per sample, with no catch-up burst. Changing cadence uses the existing last-notice time. Generated notices are attempts, not proof of Windows delivery; failures remain visible.

Offer an immediate 5–1440 minute snooze (initial input 60) and Resume in Storage, separate from Save settings. Snooze suppresses all disk-space warning, escalation and reminder notices in the current Relight instance, while sampling, tray attention, recovery and cleanup preferences remain unchanged. Show remaining minutes and the saved notification preference's continuing authority. Snooze uses monotonic elapsed time, survives Storage/dashboard closure, and ends on expiry, explicit resume or Relight exit; it is not persisted or shared between sign-ins. Snooze/restart never enables a saved notification preference that is off. After snooze ends, a new known low-space reading can generate one fresh notice even when reminders are off; unknown/healthy readings cannot. Wall-clock changes must not accelerate either cadence or snooze. These controls do not enable any cleanup rule.

### S7 — Top-level Storage workspace

**Authorized implementation — 29 October 2026:** Give Storage its own dashboard navigation entry (Ctrl+5), separate from general Settings, with **Overview**, **Space usage**, **Cleanup rules** and **Cleanup history** sections. Overview shows current per-drive capacity/severity/timestamps, automatic-cleanup status and snooze/resume. Space usage contains explicit offender scans and growth comparisons. Embed the existing reviewed rule/preview/confirmation flow and read-only run history in their respective sections. Keep thresholds, check interval, notification preference and reminder cadence in a **Monitoring settings…** dialog launched from Storage. General Settings retains application-wide preferences.

Navigating or opening the dashboard never starts a folder scan or cleanup, creates approvals or changes preferences. Keep operational views across navigation so an in-flight operation is not duplicated. Scans and confirmed reviewed work may continue across navigation/dashboard hiding; provide Cancel scan / Cancel current work, and cancel between operations on Relight exit. Repreview remains possible after cancellation. Existing single-use preview authority, exact rule approvals, default-Cancel permanent deletion confirmation, execution limits, evidence bounds and shared work gate remain unchanged. Settings dialog closure discards unsaved preference edits without stopping monitoring. Disk-space balloon activation and a tray Storage entry route to the Storage overview. Actual Windows display/click acceptance must be recorded separately from handler wiring.

### S8 — Configurable scan budget and prominent partial-coverage warnings

**Authorized implementation — 29 October 2026:** Persist an entry budget (files and directories, including the selected root) in storage preferences: default 100,000; range 1–10,000,000. Expose it directly in Storage / Space usage with Save scan limit and in Monitoring settings. Validate before saving; changes apply to the next explicitly started scan, never resume an old traversal or affect cleanup authority. Explain that larger budgets take longer and use more memory. Snapshot the budget at scan start and persist the actual budget with the result; legacy snapshots/preferences default to 100,000. Keep result/detail/baseline/depth bounds unchanged.

If traversal stops at its budget, pin a high-visibility **SCAN STOPPED — ENTRY LIMIT REACHED** banner above the Space usage scroll area. Include inspected count, actual scan budget, root, incomplete ranking explanation and increase-limit/select-smaller-folder guidance. Label file/folder rankings as partial. Show one acknowledgement dialog for a newly completed limit-hit scan while Storage is visible; do not steal focus from a hidden dashboard. Keep the banner on saved/reloaded results and across navigation, cancellation, failed scans and preference changes until replacement results establish different coverage. Other incomplete coverage gets its own persistent warning without falsely claiming the entry limit was reached. Saving a higher limit alone must not present an older partial scan as complete.

### S9 — Active scan presentation

**Authorized implementation — 29 October 2026:** While a requested scan is waiting for the shared worker, traversing or saving results, make that operation the prominent Space usage content: selected root, current stage, inspected-entry count, captured budget, indeterminate activity indicator and Cancel (disabled once bounded result saving begins). Do not imply a percentage of the drive has been scanned from the entry budget. Loading saved results alone is not an active scan.

Temporarily hide prior result tables, comparisons, timestamps, coverage warnings and setup controls during active work. Retain the previous snapshot in memory/on disk, rather than clearing or relabeling it. A successful replacement shows its own coverage; traversal cancellation/failure restores the prior warnings/results with their original root and timestamp and a retained-results status. Completed traversal with a save failure still displays current measurements and the unsaved-baseline problem, as in S2. S9 qualifies S8's persistent warning: it remains attached to the prior result, temporarily hidden while the new operation is prominent. Navigation/hiding must not start or duplicate work, and traversal/accounting/cleanup authority remain unchanged.

### S10 — Prior workload and approximate scan ETA

**Authorized implementation — 29 October 2026:** During an active scan, keep the latest prior same-root scan's measured-file count, inspected-entry count, timestamp and coverage visible as compact context. Reuse counts already present in the existing latest baseline across Relight restart; do not retain an unbounded multi-root inventory or revive old result tables during scanning. Match canonical lexical roots case-insensitively, including trailing separators. File paths measured and total files/folders inspected must be labeled separately; remaining traversal work uses the latter.

Estimate remaining observed entries from a prior traversal that finished without an entry-limit stop, bounded by a lower current entry limit when applicable. A finished traversal with skipped coverage may support an explicitly labeled observed-work estimate, never a full-drive coverage promise. Prior entry-limited/interrupted coverage is only a lower bound and cannot provide a whole-scan ETA. A different/missing prior root means unknown work. If the current count reaches/exceeds the historical target before traversal ends, return to unknown instead of displaying zero remaining or 100% complete. Lower-budget estimates must say time/work **to entry limit**, not full coverage.

Use the current run's average inspected-entry rate with monotonic elapsed time starting after acquiring the shared worker; exclude queue waiting. Require at least 250 observed entries and two seconds before a numeric ETA, suppress ETA after 30 seconds without progress, and do not guess persistence time during saving. Update elapsed/remaining text once per visible UI second and on progress, with approximation/changed-contents language. The activity bar remains indeterminate; no prior count is a guarantee of the current tree. Keep Cancel in a pinned header with scrollable estimate details. Cancellation/failure retain the existing prior baseline/counts and do not affect cleanup authority.

### Storage acceptance

These IDs extend, rather than replace, AT-01–AT-30.

| ID | Scenario | Required outcome |
|---|---|---|
| DSAT-01 | Percent/absolute thresholds, equality and invalid values | Either limit triggers; equality alone does not; invalid settings are rejected. |
| DSAT-02 | Repeated low readings, warning escalation, unavailable gap and recovery | No repeat notice within a severity/episode; critical escalates once; unknown does not rearm; recovery requires margin. |
| DSAT-03 | Multiple drives including unavailable drive | Other readings remain available; unknown is explicit; no false empty-space alert. |
| DSAT-04 | Save/restart, stale writes, invalid configuration | Settings persist, legacy configuration gains defaults, stale writes fail, last-good behavior remains intact. |
| DSAT-05 | Close/reopen dashboard and storage window, disable/mute | Resident sampling continues; disable/mute behave independently of recovery; UI remains responsive. |
| DSAT-06 | Windows tray notification and lifecycle | Safe simulated readings exercise warning/critical delivery and tray state; Windows suppression and sleep behavior documented. |
| DSAT-07 | Known tree, hard links, sparse files | Logical and allocated sizes are distinguished; allocation is deduplicated and rankings identify disk consumers. |
| DSAT-08 | Inaccessible/changed entries, partial enumeration, reparse/cloud paths | Partial results and coverage issues are explicit; excluded locations are not traversed or hydrated. |
| DSAT-09 | Cancellation, overlapping requests and resource limits | Traversal stops between calls, prior baseline survives cancellation, no overlapping worker, entry/depth/result bounds hold. |
| DSAT-10 | Baseline load/save/corruption and growth | Same-root comparable paths show growth; missing/partial paths have unknown deltas; evidence is preserved and failures are visible. |
| DSAT-11 | Native Storage window scan/rescan/reopen | User can scan a controlled folder, see rankings and growth, cancel, and reopen saved results while recovery/capacity monitoring remain independent. |
| DSAT-12 | Reviewed rules and preview | Filters, retention, age and caps apply; save/preview never delete; stale/corrupt rules are rejected and preserved. |
| DSAT-13 | Reviewed execution | Only selected, freshly verified files are deleted once; unselected, changed/locked files and junction replacements survive. |
| DSAT-14 | Cleanup authority and audit | Protected paths, unknown guards, incomplete/expired previews and failed pre-dispatch accounting block deletion; audit failures stop remaining files. |
| DSAT-15 | Automatic consent/defaults | Off by default; fresh saved-rule preview and explicit confirmation are needed; Cancel does not enable; edits revoke approval. |
| DSAT-16 | Trigger, timing and ownership | Unknown/high capacity, busy storage, unelapsed interval or unavailable worker lease cannot dispatch; reservation survives restart and clock jumps do not bypass the monotonic delay. |
| DSAT-17 | Revocation and outcomes | Disable stops pending work, per-file approval is rechecked, caps/retention still apply, and bounded history distinguishes automatic outcomes and unknown pending results. |
| DSAT-18 | Cleanup history and selected run details | Older retained runs show original outcomes/settings; filters and refresh work; byte/count bounds hold; legacy/evicted, corrupt and unfinished evidence is explicit; read-only browsing grants no cleanup authority. |
| DSAT-19 | Reminder cadence, snooze and clock boundaries | Defaults stay quiet after initial severity notices; configured reminders respect exact monotonic intervals, escalation remains immediate, unknown/muted readings do not rearm, snooze quiets all severities, expiry/resume uses fresh readings, and no catch-up burst or wall-clock acceleration occurs. |
| DSAT-20 | Reminder settings and resident snooze controls | Cadence validates and persists with legacy defaults; invalid changes preserve saved preferences; snooze survives window close/reopen, shows remaining time, resumes explicitly, and never alters recovery or cleanup authority. |
| DSAT-21 | Top-level Storage navigation and embedded workflows | Four sections and monitoring-preference dialog are reachable; general Settings has no storage section; navigation starts no scan/cleanup and creates no authority; existing scan/growth, confirmation, history and snooze workflows remain functional across navigation/hiding. |
| DSAT-22 | Configurable traversal budget and prominent stops | Saved budgets validate/round-trip and control traversal beyond the former cap; actual budget persists with legacy defaults; pinned warning, acknowledgement and partial headings appear at a stop; saved partial coverage remains visible until replaced by a rescan. |
| DSAT-23 | Active scan replaces old presentation | Waiting/traversing/saving show current progress and cancellation instead of old results/warnings; completion shows new results, cancellation/failure restore labeled prior evidence, and loading saved results is not presented as an active scan. |
| DSAT-24 | Prior workload and estimated remaining time | Same-root persisted counts feed an approximate observed-work ETA; directories are counted correctly; queue/wall-clock changes cannot inflate or accelerate timing; partial/different-root, warmup, stalled, surpassed-reference and saving phases remain explicit; Cancel stays visible. |

See [storage checkpoint](storage-checkpoint.md) for actual evidence and remaining acceptance.
