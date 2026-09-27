# Documentation index

Relight is a C# Windows application with a WPF/MVVM dashboard and resident system-tray monitoring. Start here to find requirements and implementation work without reading every document.

## Document inventory

| Document | Purpose | Read when |
|---|---|---|
| [PRD.md](PRD.md) | Product purpose, Version 1 scope, exact recovery semantics, requirements, defaults, architecture proposals and acceptance scenarios. Draft dated 27 September 2026. | Deciding what the product must do or checking whether behavior is correct. |
| [development-plan.md](development-plan.md) | Incremental milestones, dependencies, deliverables, exit criteria, acceptance-test ownership and the public GitHub repository checkpoint. The initial shell slice is implemented; full milestones remain open. | Choosing the next implementation slice, checking readiness or recording delivery progress. |
| [architecture.md](architecture.md) | Selected .NET/WPF foundation, shell lifetime, tray and single-instance design, and boundaries for the future engine. | Modifying the shell or introducing the next components. |
| [shell-checkpoint.md](shell-checkpoint.md) | Initial shell deliverables, verification evidence and outstanding interactive/acceptance checks. | Understanding exactly what works in the preview and what still needs testing. |
| [target-findings.md](target-findings.md) | Read-only inventory of both installed ChatGPT desktop packages, their activation IDs and outstanding M0 questions. | Building or testing an installed-app adapter. |
| [engine-checkpoint.md](engine-checkpoint.md) | Implemented recovery model, state/configuration stores, per-profile coordinator, executable adapter, controllable test target, test evidence and remaining integration work. | Continuing M1–M3 or reviewing attempt-accounting claims. |
| [../README.md](../README.md) | Build/run instructions, preview controls, smoke checks and project layout. | Getting a checkout running locally. |
| [../AGENTS.md](../AGENTS.md) | Repository-wide engineering rules, recovery invariants, verification and documentation expectations. Located at repository root. | Starting any agent task or reviewing a change. |

The PRD is the product source of truth. The development plan expands its delivery phases rather than replacing its requirements. WPF and system-tray residency are confirmed by the user; unresolved product choices remain identified in the PRD and milestone M0.

## Find the relevant requirements

Section numbers below refer to headings in [PRD.md](PRD.md). Search for the section title or requirement/test ID when needed.

| Question / task | PRD location | Plan milestone |
|---|---|---|
| Purpose, application recovery versus job continuation, Version 1 boundaries | Sections 1–2, 16–17 | M0 |
| Resurrector findings, upstream evidence and licensing | Section 3 (`R1`–`R10`) | M0, M9 |
| First setup, overnight recovery, maintenance and investigation journeys | Section 4 | M5–M9 |
| Default intervals, allowed ranges and terminology | Section 5 | M1–M3, M6 |
| Lifecycle states, overlays, attempt accounting and rearming | Section 6 | M1–M3 |
| Executables, installed apps, identity, helpers and launch handoff | Section 7, `APP-01`–`APP-07` | M0, M4, M6 |
| Polling, concurrency, observation, sleep/resume and restart | Section 7, `MON-01`–`MON-10` | M1–M4, M8 |
| Manual controls, safe stopping, live edits and configuration validation | Section 7, `CTL-01`–`CTL-06` | M2, M3, M6 |
| Single instance, current-user startup, Explorer and tray lifecycle | Section 7 `CTL-07`; Section 8 “Tray” | M5, M8 |
| Dashboard, editor, history, settings and accessibility | Section 8 | M5–M7 |
| Event schema, rotation, retention, redaction and diagnostic failures | Section 9, `LOG-01`–`LOG-07` | M2, M7, M8 |
| Configuration/state schemas, migration, monotonic time and session safety | Section 10 | M0–M3, M8 |
| Component boundaries, WPF/MVVM, adapters and distribution | Section 11 | M0, M1, M5, M9 |
| Timing, scale, CPU, memory, responsiveness and soak targets | Section 12 | M8–M9 |
| Acceptance scenarios and testing strategy | Section 13, `AT-01`–`AT-30` | Plan acceptance coverage table |
| Original delivery phases and release gates | Section 14 | M0–M9 mapping in plan |
| Deferred scheduling, backoff, probes, self-recovery and integrations | Section 15 | Post-Version 1 backlog |
| Open choices: actual target, Windows support and defaults | Section 16 | M0 |

## Suggested reading paths

- **New task:** root agent guidance → this index → relevant PRD sections → active plan milestone.
- **Recovery bug:** PRD Sections 5–7 and 10 → matching Section 13 acceptance scenarios → plan M1–M4/M8.
- **WPF or tray work:** PRD Sections 7–8 and 11 → plan M5–M7; preserve the Section 6 engine semantics.
- **Release readiness:** PRD Sections 12–14 → plan M8–M9 and acceptance coverage table.

## Keeping the index useful

Add or update an inventory row whenever a document is created, renamed, moved or materially changes purpose. Keep links relative and section references accurate. List only documents that exist; planned deliverables remain in the development plan until created. Store each decision or result in one clear source and link to it instead of copying requirements across files.
