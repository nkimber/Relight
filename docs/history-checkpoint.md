# History checkpoint

**Date:** 27 September 2026  
**Scope:** First read-only M7 slice; M7 exit criteria are not met.

The History page now reads Relight-owned JSON Lines files under `%LOCALAPPDATA%\Relight\Logs` without stopping monitoring. It shows the latest 500 matching events with local-time labels, severity, event type, profile, state/attempt summary and expandable UTC/correlation details. Filters cover configured application, minimum severity, event type, optional episode ID and three time ranges (24 hours, seven days, all retained). Refresh is explicit; opening History requests a fresh load. The original log folder can be opened from the page.

The storage reader accepts only the journal's exact owned filename pattern and skips reparse-point files. It de-duplicates event IDs, reports malformed lines and returns the latest matching records with a total count, so truncation is visible. A vanished file during retention is skipped. Other I/O failures reach the page as an error. Reads run away from the WPF dispatcher and a superseded read is cancelled.

**Verified locally:** `dotnet test Relight.slnx -c Release --no-restore` passed 72 tests, including reader filtering, latest-result truncation, foreign-file exclusion and corrupt-line reporting. `dotnet build Relight.slnx -c Release` passed with zero warnings/errors. `pwsh -File scripts/Test-Shell.ps1` passed the isolated tray lifecycle checks. The History page has not received an interactive visual/accessibility check. CI evidence is pending at this checkpoint.

**Remaining:** The page only offers current configured profiles in the application filter; removed-profile history still appears in the unfiltered list but cannot be selected by profile ID in the filter. There is no user-facing overnight summary, full selected-episode export, diagnostic bundle, notification system, or complete event taxonomy/error categorization. The 500-row view is intentionally bounded and cannot establish AT-25's complete selected export. History is therefore useful for inspecting recent evidence but does not yet meet the PRD's complete morning investigation flow.
