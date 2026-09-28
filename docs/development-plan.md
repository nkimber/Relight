# Relight development plan

**Created:** 27 September 2026  
**Status:** Initial shell implemented; recovery milestones remain open  
**Requirements:** [PRD.md](PRD.md)  
**Engineering rules:** [../AGENTS.md](../AGENTS.md)

Build a C# Windows application with a WPF/MVVM UI and a persistent system-tray presence. Deliver small, demonstrable increments, with recovery correctness established before enabling unattended use. Closing the dashboard leaves monitoring active; exiting Relight leaves target applications running.

This plan expands PRD Section 14 without changing Version 1 scope. Proposed defaults remain those in PRD Section 5. The actual target application, activation mechanism and job-resumption behavior require evidence before integration can be considered complete. No calendar estimates are committed before M0.

## Sequence and tracking

| Milestone | Demonstrable outcome | Depends on | Status | PRD delivery phase |
|---|---|---|---|---|
| M0 | Actual target can be identified and activated in a controlled spike | — | In progress: ChatGPT selected; read-only main/helper discovery and activation of its already-running package verified. Activation from absence and job-continuation evidence pending | 0: target validation |
| M1 | Deterministic recovery model and controllable test application | M0 | In progress: model and controllable test target implemented; full exit coverage pending | 1: recovery engine |
| M2 | Trustworthy state persistence and restart reconstruction | M1 | In progress: runtime state/configuration stores, event journal and last-good repair; shared-budget/session-state repositories, archive/tombstone protection against known older state writers, narrow evidence-matched interrupted-migration repair, same-session reconciliation, stale-configuration guard, dispatch-time configuration lease and periodic reload pass controlled tests. Cross-process pre-marker storage, fake in-flight coordinator, configuration-lease, final-attempt budget race and host-level manual/automatic-dispatch probes passed. A process-interruption probe covers all five durable migration boundaries. Explicit repair of an untrusted same-sign-in checkpoint preserves its bytes and shared budget, then reloads paused. A missing/corrupt shared budget can be replaced through an explicit new disabled profile ID while preserving old evidence/history; the old count is never inferred or silently reset. Both WPF actions need interactive acceptance. Brief shared-budget lock contention is retried without masking prolonged/inaccessible storage. Ordinary host startup now uses guarded migration and the shared-session path; isolated preview remains available. Full older-app/real-target rollout validation and real two-session evidence remain pending | 1: recovery engine |
| M3 | Working executable recovery loop with exact attempt limits | M2 | In progress: coordinator, scheduler, launch gate, executable host and durable Start now/pause/reset commands tested. Isolated WPF shared-session process tests preserved a disposable target and charged budget across a forced Relight exit, rediscovered it without duplication, reached exact lockout after three short-lived launches with no fourth dispatch, and rearmed only after a stable external start while preserving lockout during early observation. Full acceptance pending | 1: recovery engine |
| M4 | Production target adapter handles real application identity | M3 | In progress: selected ChatGPT package-family/main-process discovery and AUMID activation integrated with host; live registration/adoption without termination verified. Handoff, absence/recovery, update and job-continuation tests pending | 1 / 2: engine and integration |
| M5 | Resident WPF tray shell displays live protection status | M3 | In progress: live host status/countdowns, event-backed last-outage/stable-auto-recovery fields, dashboard status filters/sorts, prioritized tray tooltip/icon, current-user sign-in and independent pause/resume-all controls. A controlled WPF process test completed five second-instance dashboard reopen/close cycles with the tray process alive; Explorer restart, tray resources, configured-profile cycles and full UI acceptance remain pending | 2: native tray application |
| M6 | Complete setup, editor and explicit application controls | M4, M5 | In progress: executable browse/drop and running-unpackaged-process selection with fresh detection, argument-safe launch arguments/working directory, and selected ChatGPT registration; setup offers initial automatic start with policy preview and confirmation; per-profile Start now/pause/resume/enable/disable/remove/reset/duplicate disabled, with Restart now/Stop and pause for executables; a ChatGPT-specific explicit stop adapter is connected to the host but its WPF controls stay disabled pending live disposable-session validation; name/policy/executable launch-identity editor with live policy preview and automatic-start confirmation, saved-profile Test launch with fresh discovery, last-good configuration repair and sign-in preference. UI Automation verified first-run executable and selected ChatGPT registration, both initial-start warning choices, name/attempt-limit editing with unchanged ID/budget, Test launch without a duplicate or automatic charge, and pause/resume/disable/re-enable/remove of a running disposable target with target survival and unchanged charged budget. Declined/canceled confirmations left configuration unchanged; removal retained history. Remaining UI journeys, queued-dispatch cancellation, layout, screen-reader/scaling and real-target close/relaunch acceptance pending | 2: native tray application |
| M7 | Searchable overnight history, notifications and local export | M6 | In progress: local event reader, History filters including retained removed profiles, event-based summary, CSV/text export and redacted local diagnostic ZIP; live recovery/lockout tray notices and per-profile preferences. Structured launch and discovery failure categories with available native codes are exported; live adapter-failure evidence, interactive notification delivery and complete summaries pending | 2: native tray application |
| M8 | Windows lifecycle, concurrency and failure hardening verified | M7 | In progress: deterministic missed-retry and detection-gap protections; a 50-profile scheduler test shows independent deadlines and budgets with one discovery blocked. An isolated 20-profile, 15-minute closed-dashboard run met provisional CPU and working-set targets on one documented PC. Controlled corrupt-budget and inaccessible-budget-lease tests suspend automatic dispatch; the former continues external discovery with a detection-only dashboard state. A blocked log path test shows automatic recovery continues while the dashboard reports degraded logging. Actual sleep/unlock, cross-session, broader fault, active-recovery performance and soak acceptance pending | 2 / 3: acceptance and pilot readiness |
| M9 | Validated overnight pilot and reproducible x64 release package | M8 | Preparation in progress: a self-contained x64 preview ZIP with notices, manifest and checksum passed local checks; a disposable two-profile WPF soak harness passed a three-minute handoff/helper check. Release acceptance, clean-machine test, upgrade/removal, full 24-hour soak and real-target pilot pending | 3: pilot and distribution |

The default execution order is the table order. M5 may follow M3 while target-adapter work remains open, but M6 cannot pass without M4. Foundational event logging begins in M2; M7 adds the complete user-facing history and export experience.

**Authorized initial slice:** The user requested building through the first shell and publishing its GitHub repository. Solution scaffolding and the basic WPF/tray shell were therefore brought forward. See [the shell checkpoint](shell-checkpoint.md) for delivered behavior and verification, and [architecture decisions](architecture.md) for the selected foundation. This does not waive target-validation, engine or full M5 exit criteria.

**Recovery foundation in progress:** See [engine checkpoint](engine-checkpoint.md) for the model, stores, coordinator, scheduler and executable host, and [installed target findings](target-findings.md) for the two distinct packaged apps found on this machine. Neither document establishes that M0–M3 exit gates have passed.

The [session-coordination design](session-coordination.md) describes the remaining M0/M2 cross-sign-in protocol and its validation gate. It is not an implementation claim.

For each milestone, record status, delivered changes, acceptance evidence and unresolved issues here or in a linked result document. Use Not started, In progress, Blocked or Complete. A completed milestone needs its exit criteria met; a prototype or passing subset is not a release claim. Add newly created evidence/design documents to [index.md](index.md).

## GitHub repository checkpoint

**User instruction:** After creating the application shell, create a public GitHub repository in the user's personal account. Use `Relight` as the repository name if available. Creation and the initial source push are authorized; no additional visibility approval is needed.

Trigger this checkpoint when the first buildable WPF/tray shell runs successfully, without waiting for the full dashboard or all M5 acceptance criteria. If shell scaffolding is introduced earlier, perform the checkpoint then.

- Verify the authenticated GitHub account and check for an existing `Relight` repository. Do not overwrite or repurpose an unrelated repository if that name is already taken.
- Initialize local Git if needed, add appropriate .NET/IDE/build-output ignores, and include the shell source, project files, README/build instructions and product documentation in the initial commit. Exclude credentials, machine-local settings, runtime state, logs and generated output.
- Create the public repository, configure the remote and push the initial commit. Verify the remote contents and visibility, then record its URL here and in the README.
- If authentication or a repository-name collision blocks creation, preserve the completed local shell and commit, record the specific blocker and request only the information needed to resolve it.

**Status:** Complete for the initial shell checkpoint. Source and documentation are pushed to the public [nkimber/Relight](https://github.com/nkimber/Relight) repository on `main`; remote visibility and the pushed commit were verified. This checkpoint does not mark the full M5 milestone complete. See [shell verification evidence](shell-checkpoint.md).

## M0 — Validate the target and settle the foundation

**Outcome:** The highest-risk integration is understood before substantial UI work.

Work:

- Use the user-confirmed ChatGPT package (`OpenAI.Codex_2p2nqsd0c76g0`) as the only initial installed-app target; exclude ChatGPT Classic. Inspect this installation and record stable launch and running-process identities, package status, handoff behavior and helper/main-process rules.
- Build a disposable discovery/activation spike. Demonstrate discovery of an already-running instance and activation followed by discovery of the real target, without relying on the launcher's PID or version-specific installation path.
- Verify application recovery separately from continuation of a disposable representative job. Record unsupported or unknown continuation behavior explicitly; do not add workflow automation.
- Confirm the Windows support baseline, select a supported .NET LTS/SDK, and review WPF tray/notification dependencies and licenses. WPF/MVVM and tray residency are fixed requirements.
- Resolve or record ownership of PRD Section 16 choices. Validate the proposed defaults, initial-start policy and observation duration before finalizing their acceptance/UX.
- Document component boundaries and the approach to session-scoped live state, shared configuration revisions and cross-sign-in lockout. Identify how durable reservation writes will be implemented and verified on Windows.

**Deliverables:** Target/adapter findings, initial architecture decisions, selected toolchain and remaining decision log. Create and index these documents when the work is performed.

**Exit:** A demonstrated target launch/discovery contract, a recorded application-recovery versus job-continuation result, and enough decisions settled to implement the engine. An unsupported activation mechanism blocks the target integration gate; it cannot be deferred beyond Version 1 if the actual target requires it.

## M1 — Establish the solution and executable recovery model

**Outcome:** Recovery rules can be exercised deterministically without controlling a real job.

Work:

- Create the solution, pin the selected SDK, enable nullable analysis and document actual build/test commands. Use logical components for domain, coordination, Windows adapters, storage and WPF presentation; split projects only where dependency boundaries justify it.
- Define stable profile IDs, policy validation, lifecycle states, pause/detection overlays, observation provenance, episode/operation IDs and explicit command/event contracts.
- Implement a pure state/policy model against fake clock, discovery, launch and repository interfaces. Cover retry delay, appearance timeout, uninterrupted observation, zero attempts, external rearming and live-policy semantics.
- Create a small Windows test application that supports stable running, timed exit, helpers, launcher handoff and controllable graceful-close behavior.
- Write deterministic tests for attempt 3 succeeding versus failing, no launch 4, external starts during retry/lockout, auto-rearm disabled and unknown detection. Simulate races and time boundaries without wall-clock sleeps.

**Exit:** The state model demonstrates PRD Section 6 transitions and accounting with deterministic checks. Model coverage includes AT-01–AT-10, AT-27 and AT-28; Windows behavior remains unverified until later milestones.

## M2 — Make configuration and recovery state durable

**Outcome:** Restarting Relight cannot silently replenish a recovery budget.

Work:

- Implement versioned profile/global configuration, atomic saves, last-good backup, revision checks and validation for startup and live edits.
- Implement durable attempt reservation, lockout and runtime snapshots separately from policy/history. Distinguish new-profile initialization from missing or damaged state for an existing profile.
- Reconstruct state conservatively after restart; load then discover before allowing a dispatch. Preserve interrupted reservations, lockout, pause/armed state and monitoring gaps.
- Implement the M0 session/configuration coordination design, including persisted lockout across sign-ins. Keep UTC history separate from monotonic in-session deadlines.
- Establish the structured event schema and append pipeline, bounded fallback buffer and degraded diagnostics. A failed durable reservation must prevent launch dispatch.
- Exercise failures before/after reservation, process interruption, corrupt/missing state, stale revisions and failed migrations; preserve original evidence on failure.

**Exit:** Repository/coordinator tests cover AT-11, AT-14, AT-15, AT-23 and AT-30. A crash after reservation cannot grant an extra launch. Invalid state cannot masquerade as a new profile, and invalid live configuration retains the last-good settings.

## M3 — Deliver a working executable watchdog

**Outcome:** A test application can recover, exhaust its budget and rearm after a stable external start.

Work:

- Implement executable discovery and argument-safe launch with current-session identity, canonical path and PID/start-time checks. Represent present, absent, ambiguous and unavailable outcomes distinctly.
- Connect the model and durable repository through a serialized per-profile coordinator. Use cancellation, fresh discovery before dispatch, operation IDs and a shared scheduler/global launch queue.
- Implement normal/observation/lockout polling, absence confirmation, appearance timeout and optional exit-event acceleration. Preserve polling as authoritative reconciliation.
- Support multiple independent profiles and cancellation on pause/disable/remove/quit. Never bind target lifetime to the supervisor's process or handle lifetime.
- Implement initial adoption, wait-for-first-start, automatic recovery, lockout discovery, external-start observation and rearm, including zero-attempt and rearm-disabled policies.
- Add coordinator commands for explicit actions and policy edits so the later UI uses the same verified paths. A test harness can drive these commands before the dashboard exists.

**Exit:** AT-01–AT-12, AT-14, AT-19, AT-21, AT-24 and AT-27–AT-29 pass at the relevant engine/Windows integration level. Demonstrate exactly three failed automatic launches, no fourth, and stable external recovery. Interrupt Relight and confirm the test target survives and budget restoration works.

## M4 — Integrate the actual target and logical application identity

**Outcome:** Recovery works for the user's real installation, including launcher handoff and helpers.

Work:

- Turn the M0 spike into supported launch/match adapters. Add installed-app activation when required by the actual target.
- Resolve stable installed identity at launch; handle application update paths, launcher exit and main/helper roles. Document when a verified handoff preserves logical identity and when observation must restart.
- Prevent ambiguous or overlapping enabled profiles from controlling the same target. Offer actionable unavailable/permission diagnostics without repeated UAC prompts.
- Test adapters with the controllable target and then the actual installed application in a safe, disposable session. Capture native failure categories and useful error codes.

**Exit:** AT-16–AT-19, AT-21 and AT-26 have integration evidence. Actual application launch, real-target detection and observation succeed. Job continuation is reported independently and is never inferred from process presence.

## M5 — Build the resident WPF tray shell

**Outcome:** Relight stays in the tray and the dashboard reflects engine state without controlling its lifetime.

Work:

- Implement WPF/MVVM composition and explicit application shutdown. Start with no active profiles; show an honest empty state.
- Once the initial WPF/tray shell builds and runs, complete the GitHub repository checkpoint above if it has not already been completed.
- Add the tray icon, prioritized summary/tooltip and menu: dashboard, add application, pause/resume all, history, current-user sign-in startup and exit. Keep commands unavailable until their corresponding behavior is implemented.
- Implement one engine instance per user interactive session. A second launch activates the existing dashboard; Explorer restart recreates the icon.
- Build dashboard rows, filters/sorting, details and countdowns from engine snapshots. Explain paused, detection-unavailable and intervention states in words as well as color.
- Ensure all disk/process work stays off the dispatcher. Recoverable notification/view failures must not stop coordination. Add baseline keyboard navigation, labels and theme/scaling support now.

**Exit:** AT-22 passes. Closing and reopening the dashboard leaves monitoring active; explicit exit stops monitoring and leaves targets running. Repeated window cycles do not create duplicate coordinators or leak tray resources. Live state/countdowns remain responsive during recovery.

## M6 — Complete registration, configuration and user controls

**Outcome:** The user can safely configure and manage protection through WPF.

Work:

- Implement executable browse/drop, running-process and installed-app selection. Show resolved identity, launch method and Detect now results before enablement; block overlapping enabled profiles.
- Add editor sections for identity/launch, monitoring, recovery and notifications, with readable units, PRD ranges and a plain-language policy preview.
- Implement create/edit/duplicate/disable/remove with immutable IDs. Duplicates start disabled. Save/enable explains an automatic-start consequence when configured; Test launch performs fresh discovery first.
- Connect Start now, Restart now, Pause/Resume, Stop and pause, Reset recovery, Edit, Remove and View history. Use acknowledged asynchronous operations, applicable action states and progress/cancellation where appropriate.
- For explicit stopping, reverify identity, attempt graceful close and request a force-close choice after timeout. Preserve default leave-running behavior for pause/disable/remove/exit.
- Complete live-edit semantics: restart changed observation duration, recompute retry delay from save time, preserve in-flight policy snapshots, and cancel pending launches/reconcile after identity changes.
- Add configuration repair/degraded-state presentation, settings and current-user startup preference. Verify keyboard/screen-reader access and 100–200% scaling through complete setup and maintenance journeys.

**Exit:** AT-12–AT-15 pass end to end, and AT-01, AT-02, AT-09, AT-19, AT-27 and AT-28 are verified through the UI. Save never silently terminates a target or clears a budget. First-run, maintenance and force-close choices are unambiguous.

## M7 — Make overnight outcomes inspectable

**Outcome:** The morning history explains recovery and suspension using durable evidence.

**Current evidence:** [History checkpoint](history-checkpoint.md) records the first read-only browser and its limits. M7 exit criteria and AT-25 remain open.

Work:

- Complete required structured events and error categories, with profile/episode/operation linkage, UTC timestamps, origin, attempt accounting and native error information.
- Implement asynchronous history loading/filtering by application, severity, event type, episode and time; detail expansion; local/UTC presentation; copy details and open log folder.
- Add overnight summaries for outages, attempts, stable recoveries, lockouts and known unavailable duration. Label monitoring gaps unknown instead of treating them as confirmed downtime.
- Implement bounded rotation, age/size retention, plain-text/CSV selection export and redacted diagnostic bundles to a user-selected local destination.
- Deliver non-blocking recovery/lockout notifications with one intervention notification per episode and preference controls. Expose logging failure and bounded-buffer status visibly.

**Exit:** AT-25 passes and complete event chains explain AT-03–AT-11. Retention touches only owned log files, exports preserve selected episode context, redaction is verified, and notification failure does not delay recovery.

## M8 — Harden Windows lifecycle and failure handling

**Outcome:** The complete application preserves safety and timing through operational disruptions.

Work:

- Verify sleep/resume, session lock/unlock, clock changes, sign-in/restart and concurrent signed-in sessions. Reconcile after gaps without catch-up bursts or unearned observation time.
- Stress concurrent polls, callbacks, UI actions, launch timeouts and simultaneous target failures. Confirm stale completions cannot authorize a duplicate launch or wrong-target termination.
- Inject disk full/write denial, log failure, corrupt state/configuration and migration failures. Verify degraded UI and continued passive detection while unsafe automatic actions stay suspended.
- Measure PRD Section 12 timing, 50-profile isolation, idle CPU with 20 ordinary profiles, closed-dashboard working set and action acknowledgement. Record reference hardware and measurement methods; meet targets or explicitly resolve deviations before release.
- Finish accessibility review for keyboard, screen readers, light/dark/high-contrast themes and scaling. Check current-user startup enable/disable, second-instance handling and Explorer restart in the packaged application.

**Exit:** AT-10, AT-11, AT-19–AT-24, AT-29 and AT-30 pass under fault/lifecycle testing. All AT-01–AT-30 have current results or explicit blockers; no unresolved duplicate-launch, budget-loss or unintended-termination defect advances to unattended pilot.

## M9 — Pilot, package and release Version 1

**Outcome:** A reproducible Windows x64 package has evidence supporting overnight use.

Work:

- Produce a self-contained portable Windows x64 package, version metadata and dependency/license notices. Verify build/test/publish instructions from a clean checkout and launch on a compatible machine without a separately installed .NET runtime.
- Verify extraction/upgrade/removal behavior, current-user startup registration cleanup and documented configuration/log locations. Do not silently remove user data or update during protected overnight work.
- Run a 24-hour soak with repeated controlled failures, target handoff, UI open/close cycles, concurrent profiles and bounded log growth. Measure memory/CPU and monitoring timing against the PRD targets.
- Run a supervised overnight pilot with the actual target using disposable work. Review the following morning's outage/attempt/recovery/lockout history, session-lock behavior and application-update recovery evidence.
- Publish local release artifacts with checksums, build instructions, setup/troubleshooting guidance, known limitations and an acceptance report. Publishing to external services is a separate release action when requested.

**Exit:** Every Version 1 acceptance scenario passes on the release candidate, the 24-hour soak meets the PRD reliability gate, performance results are recorded and target integration works. Application recovery versus actual job continuation is clearly reported. No unresolved launch-duplication, budget-loss or unintended-termination defect remains.

## Acceptance coverage

PRD Section 13 defines expected behavior. This table assigns initial implementation and final verification ownership; M9 reviews the complete matrix on the release candidate. Milestone checks do not replace the original scenario definitions.

| Acceptance IDs | Coverage | Initial implementation | Final focused verification |
|---|---|---|---|
| AT-01–AT-09 | Adoption, first-start, recovery cap, observation and external rearm | M1–M3 | M4, M6, M7 |
| AT-10 | Concurrent actions and one launch per profile | M1, M3 | M8 |
| AT-11 | Restart/lockout/interrupted reservation | M2, M3 | M8 |
| AT-12 | Pause/disable/remove/exit preserve targets | M3, M5, M6 | M6, M8 |
| AT-13 | Explicit stop/restart and force-close choice | M3, M6 | M6 |
| AT-14 | Rename and policy changes preserve state/history | M1–M3, M6 | M6, M7 |
| AT-15 | Invalid external configuration retains last-good state | M2, M6 | M6, M8 |
| AT-16–AT-18 | Update identity, launch handoff and helper semantics | M0, M4 | M4, M9 |
| AT-19 | Ambiguity and inaccessible detection | M3, M4, M6 | M8 |
| AT-20 | Sleep/resume and clock changes | M1–M3 | M8 |
| AT-21 | PID reuse | M3, M4 | M8 |
| AT-22 | Explorer restart and second-instance activation | M5 | M8 |
| AT-23 | Disk full and corrupted runtime state | M2 | M8 |
| AT-24 | Independent simultaneous failures | M3 | M8 |
| AT-25 | Retention and redacted export | M7 | M7, M9 |
| AT-26 | Actual installed target recovery | M0, M4 | M4, M9 |
| AT-27–AT-28 | Zero automatic attempts and rearm disabled | M1, M3 | M6 |
| AT-29 | Supervisor crash leaves targets alive | M2, M3 | M8 |
| AT-30 | Invalid migrations preserve evidence and suspend actions | M2 | M8 |

The AT matrix is necessary but not exhaustive: milestone exits also cover APP/MON/CTL/LOG requirements, UI/accessibility requirements and Section 12 operational targets that lack a dedicated AT scenario.

## Post-Version 1 backlog

Keep PRD Section 15 as the source for weekly protection windows, scheduled actions and DST semantics, bounded backoff, optional health probes, advanced shutdown, supervisor self-recovery, Resurrector import and ARM64/integrations. Promote an extension only through an explicit scope decision with acceptance criteria; do not let it delay the core watchdog release.
