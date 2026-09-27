# Agent guidance

## Scope and starting points

These rules apply throughout this repository. Read this file and [the documentation index](docs/index.md) before making changes. Read the relevant sections of [the PRD](docs/PRD.md) and the current milestone in [the development plan](docs/development-plan.md) before implementation.

Relight is a new C# Windows desktop application with a **WPF/MVVM UI and a resident system-tray presence**. Closing its dashboard must leave monitoring active. Application recovery is the Version 1 goal; resuming ChatGPT/Codex jobs is not an assumed capability.

- Follow explicit user instructions first. The PRD governs product behavior; the development plan governs implementation order. This file governs engineering practice. Surface conflicts rather than silently changing requirements.
- Treat proposed defaults in the PRD as planning defaults until confirmed. WPF and tray residency are confirmed. Do not infer the initial target is Codex from the repository name.
- Use `C:\dev\Resurrector` only as a reference when relevant. Do not modify it as part of ordinary Relight work. Retain applicable license notices if reusing source or assets.
- The repository contains a runnable WPF/tray shell and partial recovery foundation. The engine is not connected to the shell and target integration is pending. Read `docs/shell-checkpoint.md` and `docs/engine-checkpoint.md` for verified behavior; do not describe planned components or unrun checks as implemented or passing.

## Work in reviewable increments

- Implement the next agreed milestone or requested change, with its acceptance criteria. Avoid speculative frameworks, unrelated cleanup and Version 2 features.
- Inspect existing files and local instructions before editing. Preserve unrelated user changes. Do not reset, overwrite or delete work to make a check pass.
- Resolve routine implementation choices directly. Ask only when missing information materially changes product behavior or prevents reliable completion; continue independent work where possible.
- Record consequential choices and their reasons in documentation: framework baseline, target identity, launch strategy, storage/session coordination, and third-party dependencies. Mark assumptions and unverified claims explicitly.
- Keep documentation current in the same change. Add new documents to `docs/index.md`; update milestone status only when supported by evidence.

## Architecture and C# conventions

- Keep recovery policy in a UI-independent domain layer. WPF views/view models must not implement retry accounting or directly control target processes.
- Separate domain policy, orchestration, Windows adapters, persistence/logging and presentation. Use interfaces at time, process, launch and storage boundaries to support deterministic tests; avoid abstractions with no concrete need.
- Prefer one application executable initially, with explicit application lifetime independent of windows. Do not introduce a Windows service or separate engine process without a documented requirement.
- Serialize commands/events per profile; allow at most one outstanding launch per profile. Use operation IDs and cancellation to reject stale completions. One blocked adapter must not stall other profiles.
- Use a shared scheduler and asynchronous I/O. Avoid a dedicated polling thread per profile, blocking the WPF dispatcher, `.Wait()` or `.Result` on UI paths, and `async void` outside event handlers.
- Marshal bound UI changes to the dispatcher. Dispose process handles, subscriptions, cancellation sources and tray resources. Treat notification failures as independent of recovery.
- Enable nullable reference types and use normal C# naming conventions. Prefer explicit domain types for states, identity, durations and outcomes over magic strings or loosely structured dictionaries.
- Use UTC timestamps for persisted events and monotonic elapsed time for in-session deadlines. Sleep, restart and unreliable detection break observation continuity; wall-clock jumps must not accelerate recovery.
- Select a supported .NET LTS and compatible dependencies at the target-validation milestone. Pin the SDK once selected, document build/test/publish commands, and review dependency licensing. Do not invent versions or add packages for trivial functionality.

## Recovery invariants

Treat PRD Sections 5–7 and 10 as the authoritative details. Preserve these properties in every change:

1. **Unknown is not absent.** Access denied, ambiguous identity and enumeration failures suspend automatic actions; they never justify a launch or termination.
2. **Discover before dispatch.** Confirm absence, respect the retry delay, then check again immediately before launching. Adopt an existing matching instance instead of creating a duplicate.
3. **Reserve before launch.** Persist an automatic attempt durably before dispatch. A dispatch error consumes that attempt; an interrupted reservation is not silently refunded. If durable accounting cannot be trusted, suspend automatic launches and continue passive discovery.
4. **Respect the exact cap.** With a limit of three, a fourth automatic dispatch in the episode is forbidden. Attempt three still receives its full appearance timeout and observation opportunity. Zero permits detection and explicit user starts only.
5. **Stability earns recovery.** Reset a budget only after qualifying uninterrupted observation or explicit Reset recovery. A locked-out external start stays locked out until stability and the rearm policy permit release; an early exit does not earn new attempts.
6. **Budgets survive ordinary changes.** Pause/resume, disable/enable, rename, policy edits, Relight restart and sign-in must not silently clear lockout. Increasing the limit alone does not unlock a profile.
7. **Keep targets independent.** Pause, disable, remove, window close and Relight exit/crash leave target applications running. Do not use kill-on-close Job Objects for ordinary desktop targets.
8. **Termination is explicit.** Stop and pause / Restart now operate only on a freshly verified selected target, try graceful close first, and offer a force-close choice if needed. Never kill duplicates to resolve ambiguity.
9. **Identity exceeds a PID.** Match the current user's interactive session using canonical path or stable package/application identity, with PID plus start time for an instance. Account for helpers, launcher handoff, updates and overlapping profiles. Name-only matching is never the default.
10. **Manual operations remain distinct.** Record explicit Start/Restart separately from automatic attempts. An explicit launch while locked out does not itself clear lockout. Preserve the in-flight policy snapshot and documented live-edit semantics.

## Persistence, diagnostics and privacy

- Separate configuration, runtime state and event history. Use schema versions, atomic writes, last-good configuration and revision checks; preserve original files on failed migration.
- Missing state for an existing profile or corrupt state must not create a fresh budget. Keep affected automatic actions suspended until the documented recovery/reset path is used.
- Coordinate shared configuration and cross-sign-in lockout with session-scoped live state. Never use stale state to control a process in another session.
- Store application data under `%LOCALAPPDATA%\Relight` and logs under its `Logs` directory. Restrict retention deletion to owned log files; never include configuration, runtime state or exported bundles.
- Emit structured events with profile, episode and operation IDs. Use truthful language: disappearance is not proof of a crash, and external discovery is not proof a person launched the app.
- Redact sensitive arguments. Do not log credentials, full environment contents, chat text or application window contents. Diagnostic export stays local and uses a user-selected destination.
- Expose persistence/logging failures visibly. Bound in-memory buffers and log growth. Do not swallow exceptions that hide lost monitoring or accounting guarantees.
- Launch with argument-safe APIs and validated identities. Do not construct shell commands from user input or add repeated elevation prompts.

## Verification and completion

- Build with `dotnet build Relight.slnx -c Release`. Exit the running preview, then run `pwsh -File scripts/Test-Shell.ps1` for process-level shell checks. See the README for running/publishing and `docs/shell-checkpoint.md` for interactive coverage limits.
- Use a fake clock and fake adapters for state-machine, boundary-timing, concurrency and budget tests. Avoid real sleeps in deterministic unit tests.
- Use a controllable Windows test target for early exits, helpers, handoff and graceful-close behavior. Never force-close a real valuable or overnight job for testing.
- Add regression coverage for behavioral fixes, prioritizing duplicate launches, budget loss, identity errors and unintended termination. Documentation-only changes need link/content checks, not application tests.
- Run checks appropriate to the change. Unit tests do not establish Windows activation, tray recovery, accessibility or session behavior; verify those through integration/manual acceptance and record the environment.
- Track evidence against PRD acceptance IDs (`AT-01`–`AT-30`). Label checks as passed, failed, blocked or not run; include reasons for gaps. Performance and soak results must identify the machine, duration and profile count.
- Before reporting completion, inspect the final changes and check affected documentation links. State what changed, what was actually verified, and any material limitations. Do not claim a milestone complete with unmet exit criteria.

## Version 1 boundaries

Do not add automatic prompt submission, Resume clicks, sign-in automation, sleep prevention, forced restart based on an unresponsive window, automatic Windows reboot or overnight automatic updates. Scheduling, advanced probes, backoff, self-recovery, Resurrector import and ARM64 belong to the later scope unless the user explicitly changes it.
