# Performance checkpoint

This is an M8 measurement record, not a release or soak acceptance report. The source of truth for targets is [PRD Section 12](PRD.md#12-reliability-performance-and-operational-constraints).

The repeatable [performance probe](../tools/Relight.PerformanceProbe/Program.cs) creates an isolated shared-session preview under ignored `artifacts/performance`, copies a runnable disposable executable into a separate path for each enabled profile, and saves 20 profiles with the default wait-for-first-start policy. It runs `Relight.exe --shared-session-preview <data> --tray` with its dashboard closed. After 30 seconds of warmup, it samples process working set every 10 seconds and calculates process CPU time divided by monotonic elapsed time and the number of logical processors. It checks that all shared automatic-attempt budgets remain at zero and stops only the Relight process it started. Raw per-run data and `measurement.json` remain in the ignored artifact directory for local inspection.

Run from a Release build on Windows:

```powershell
dotnet build Relight.slnx -c Release
dotnet run --project tools/Relight.PerformanceProbe -c Release -- 15 20
```

Reference machine: Lenovo 20XW003HUS, 11th Gen Intel Core i7-1165G7 (4 cores, 8 logical processors), 16,885,276,672 bytes installed RAM, Windows 11 Pro build 26200. This is a normally loaded development desktop, not a dedicated idle benchmark host. Target applications are absent; their distinct executable paths are present and runnable. The probe measures resident Relight CPU and working set, not overall system load, dashboard responsiveness, target recovery timing or 24-hour growth.

The one-minute smoke run on 28 September 2026 reported 0.133% total CPU, 108.5 MiB peak working set and zero charged automatic attempts across 20 profiles. Its raw report is in ignored `artifacts/performance/20260928-080003-d9c0ba0b/measurement.json`.

The 15-minute run on the same machine and date measured **901.7 seconds** after warmup with **20 enabled, absent profiles**. Relight used **4.5625 CPU seconds**, or **0.063% of total eight-processor capacity** over the interval. The closed-dashboard working set was **104.8 MiB initially, 112.9 MiB peak and 109.0 MiB at the end**. No profile charged an automatic attempt. The raw report is in ignored `artifacts/performance/20260928-080142-bbc4fd03/measurement.json`. This run met the PRD's provisional below-1% idle CPU and below-150-MB resident working-set targets on this reference PC. It does not establish those targets during recovery, with running targets, across other machines, or over a 24-hour soak.

The Release solution build, including the probe project, passed with zero warnings/errors. [Windows CI](https://github.com/nkimber/Relight/actions/runs/36396578088) passed its Release build, deterministic tests and cross-process probes. CI builds the measurement tool; it does not run the 15-minute benchmark. That measured result is local to the reference machine above.
