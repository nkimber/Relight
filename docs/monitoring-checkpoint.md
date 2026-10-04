# Monitoring page checkpoint

**Date:** 4 October 2026  
**Scope:** User-requested main monitoring page; M5/M7 presentation increment. Full milestone acceptance remains open.

Monitoring is the initial dashboard and the tray's Open dashboard destination. It uses a read-only, virtualized WPF grid, with a row for each configured profile, including paused, disabled and unavailable profiles. A logical application is one row; ChatGPT's Electron helper processes are not separate targets. Name and status columns can be sorted. Application-page filters do not hide monitoring rows. Existing Applications, History and Settings shortcuts remain Ctrl+1–3; Monitoring is Ctrl+4. Manage applications opens the existing setup/control page.

## Metric definitions

| Column | Meaning and limits |
|---|---|
| Application / Status | Current configured name and the existing host status; row tooltip gives its next-action explanation. |
| PID | PID from a verified current-session identity using the supported Windows executable or ChatGPT adapter format. No new process enumeration or process-control permission is introduced. Unknown, disabled, paused or unavailable instances show a dash. |
| Uptime at check | Windows process age at its last verified sighting, derived from its UTC start timestamp and the host's monotonic last-sighting age. It stays frozen between sightings and is not measured availability, stability qualification, or evidence that the process is still alive. Unusable identity/timestamps show a dash. Windows timestamp/clock errors can make this age unavailable; it does not affect recovery policy. |
| Last verified | Monotonic elapsed time since the latest verified sighting in this Relight session. A dash means no sighting; an old value is deliberately not presented as a fresh check. |
| Auto relaunches (24h) | Count of retained `LaunchDispatched` events for that profile in the inclusive UTC interval from refresh time minus 24 hours through refresh time. Includes automatic initial starts and attempts that never appear or stabilize; excludes reservations, manual and external starts. This is neither successful recoveries nor every OS restart. Shared retained profile history includes other sign-ins. |
| Attempts | Reserved automatic attempts / configured cap for the current episode; separate from the rolling history count. |

The existing dashboard history scan computes counts alongside outage/recovery milestones every 30 seconds, across all retained records rather than History's 500-row display limit. The window endpoint is displayed. Read failures show Unavailable; skipped malformed lines or currently degraded logging label counts partial. Older unrecorded events cannot be reconstructed. Rows update in place on the existing dispatcher refresh; there are no new per-profile timers or recovery-policy changes.

## Validation

| Check | Result |
|---|---|
| Release solution build | Passed, zero warnings/errors. |
| Nine focused monitoring/history checks | Passed: last-sighting age freezing, unavailable/paused/disabled identities, independent row membership/navigation/removal, unknown/partial history, malformed/unsupported adapter identities, executable replacement with PID reuse, full history count beyond 500 rows, UTC boundary inclusion/future exclusion and expiration. The final focused run used a separate ignored output directory while the full suite was running. |
| Shell smoke | Passed: hidden tray startup, second-instance dashboard activation and restart after interruption. |
| Full test suite | Pending final result; existing Windows account-ID-prefix test fails on this account (`S-1-12-1-…` versus its expected `S-1-5-…`). |
| Live configured ChatGPT grid | Passed at the default window size: one ChatGPT row, Observing stability, the unchanged main PID 101912, process age about 1h 35m, advancing last-check age, zero recorded relaunches and a 0/3 episode budget. Manage applications opened the control page; History navigation and return to Monitoring were visually checked. Ctrl+4 returned to Monitoring. |
| Full accessibility, high contrast, 100–200% scaling, large-profile scrolling/performance | Not run for this increment. Virtualization is enabled, but no new performance acceptance claim is made. |
| Actual ChatGPT exit/relaunch or resumed work | Not run; this presentation change does not establish target recovery or job continuation. |

The existing slow ChatGPT identity checks can delay first adoption and repeatedly interrupt stability qualification. The grid exposes last-check age and the existing status explanation; adapter performance is outside this increment.
