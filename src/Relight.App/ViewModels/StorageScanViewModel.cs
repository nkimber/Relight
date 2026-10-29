using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.ViewModels;

internal sealed class StorageScanViewModel(string dataDirectory, SemaphoreSlim scanGate,
    Func<int>? getEntryLimit = null, TimeProvider? clock = null, ApplicationDiagnostics? diagnostics = null) : INotifyPropertyChanged
{
    private readonly StorageScanStore _store = new(dataDirectory);
    private CancellationTokenSource? _cancellation;
    private bool _closed;
    private DateTimeOffset? _lastFinishedUtc;
    private StorageScanSnapshot? _lastSnapshot;
    private ScanDiagnosticSession? _diagnosticScan;
    private readonly StorageScanEstimator _estimator = new(clock);
    public string Root { get; set; } = DiskSpaceMonitor.SystemDrive;
    public bool IsIdle { get; private set; } = true;
    public bool IsScanning { get; private set; }
    public bool ShowScanContent => !IsScanning;
    public bool ShowCoverageWarning => HasCoverageWarning && !IsScanning;
    public string ActiveRoot { get; private set; } = "";
    public int ActiveEntryLimit { get; private set; }
    public int EntriesInspected { get; private set; }
    public string ProgressSummary => $"{EntriesInspected:N0} entries inspected · scan limit {ActiveEntryLimit:N0}";
    public string PriorWorkSummary
    {
        get
        {
            var estimate = _estimator.Estimate();
            return estimate.PriorEntries is { } entries
                ? $"Prior scan {estimate.PriorFinishedUtc!.Value.ToLocalTime():G}: {estimate.PriorFiles:N0} file paths measured; {entries:N0} files/folders inspected. " +
                  (estimate.PriorComplete ? "Complete prior coverage; current contents may differ." :
                   estimate.PriorTraversalFinished ? "Prior traversal finished with coverage gaps; estimates cover observed work only." :
                   "PARTIAL prior coverage — observed counts are a lower bound, not a total.")
                : "No comparable prior count for this root in the latest scan. Remaining work is unknown.";
        }
    }
    public string WorkEstimateText
    {
        get
        {
            var estimate = _estimator.Estimate();
            string target = estimate.ToEntryLimit ? "to entry limit (not full coverage)" : "based on prior observed work";
            string elapsed = $"Elapsed: {(int)estimate.Elapsed.TotalHours}:{estimate.Elapsed.Minutes:00}:{estimate.Elapsed.Seconds:00}. ";
            return estimate.State switch
            {
                StorageEstimateState.Waiting => "Waiting for the scan worker; scan timing has not started.",
                StorageEstimateState.Saving => elapsed + "Traversal finished; saving results. Save time remaining is unknown.",
                StorageEstimateState.NoReference => elapsed + "Remaining work and time: unknown until a comparable completed traversal is available.",
                StorageEstimateState.PartialReference => elapsed + "Remaining work and time: unknown — the prior scan was incomplete.",
                StorageEstimateState.ReferenceExceeded => elapsed + (estimate.ToEntryLimit
                    ? "Scan budget target reached; finalizing traversal. Remaining time is unknown."
                    : "Previous work estimate reached or exceeded; still scanning. Remaining work and time are unknown."),
                StorageEstimateState.Stalled => elapsed + $"Approximately {estimate.RemainingEntries:N0} entries remaining {target}; time remaining unknown — no recent progress.",
                StorageEstimateState.Measuring => elapsed + $"Approximately {estimate.RemainingEntries:N0} entries remaining {target}; estimating scan speed…",
                StorageEstimateState.Estimated => elapsed + $"Approximately {estimate.RemainingEntries:N0} entries remaining {target}; estimated time remaining: {Math.Ceiling(estimate.RemainingTime!.Value.TotalSeconds):N0} seconds. " +
                    "Approximate; file counts and scan speed can change.",
                _ => elapsed + "Remaining time is unknown."
            };
        }
    }
    public string ResultsLabel { get; private set; } = "No scan results yet.";
    public bool CanCancel { get; private set; }
    public bool HasCoverageWarning { get; private set; }
    public bool EntryLimitReached { get; private set; }
    public int ScanVersion { get; private set; }
    public string CoverageWarningTitle { get; private set; } = "";
    public string CoverageWarning { get; private set; } = "";
    public string FilesHeading => HasCoverageWarning ? "Largest files found in PARTIAL scan (allocated space)" : "Largest files by allocated space";
    public string FoldersHeading => HasCoverageWarning ? "Largest folders found in PARTIAL scan (includes descendants)" : "Largest folders by allocated space (includes descendants)";
    public string Status { get; private set; } = "Choose a local folder or drive and select Scan folder. No files will be deleted.";
    public string BaselineStatus { get; private set; } = "No growth comparison yet.";
    public string ScanAge => _lastFinishedUtc is { } time
        ? $"Latest scan result: {time.ToLocalTime():G} · age {Math.Max(0, (DateTimeOffset.UtcNow - time).TotalMinutes):N0} minutes"
        : "No completed scan loaded.";
    public IReadOnlyList<StorageScanRow> Files { get; private set; } = [];
    public IReadOnlyList<StorageScanRow> Folders { get; private set; } = [];
    public IReadOnlyList<StorageGrowthRow> Growth { get; private set; } = [];
    public IReadOnlyList<StorageScanIssue> Issues { get; private set; } = [];

    public async Task LoadAsync()
    {
        IsIdle = false;
        Changed();
        try
        {
            var snapshot = await Task.Run(() => _store.Load());
            if (_closed || snapshot is null) return;
            Root = snapshot.Root;
            Present(snapshot);
            BaselineStatus = "Latest saved scan. Scan the same root again to compare growth.";
            Changed();
        }
        catch (Exception error) { if (!_closed) Status = $"Saved scan unavailable: {error.Message}"; }
        finally { IsIdle = true; if (!_closed) Changed(); }
    }

    public async Task ScanAsync()
    {
        if (!IsIdle || _closed) return;
        string root = Root;
        int limit = StorageScanSnapshot.DefaultEntryLimit;
        IsIdle = false;
        IsScanning = true;
        ActiveRoot = root;
        ActiveEntryLimit = limit;
        EntriesInspected = 0;
        CanCancel = true;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        Status = "Waiting for the scan worker…";
        Changed();
        var progress = new Progress<int>(count =>
        {
            if (_closed || !CanCancel || _cancellation?.Token != token || token.IsCancellationRequested) return;
            EntriesInspected = Math.Max(EntriesInspected, count);
            _estimator.Observe(EntriesInspected);
            Status = $"Scanning {root} · {count:N0} entries inspected…";
            Changed();
        });
        bool acquired = false;
        try
        {
            limit = getEntryLimit?.Invoke() ?? StorageScanSnapshot.DefaultEntryLimit;
            _estimator.Begin(root, limit, _lastSnapshot);
            _diagnosticScan = diagnostics?.BeginScan(root, limit);
            ActiveEntryLimit = limit; Changed();
            await scanGate.WaitAsync(token);
            acquired = true;
            _estimator.Start();
            _diagnosticScan?.Stage(DiagnosticScanStage.Traversing);
            var snapshot = await Task.Run(() =>
            {
                var result = new StorageScanner().Scan(root, token, progress, limit, _diagnosticScan);
                _diagnosticScan?.TraversalCompleted(result);
                return result;
            }, token);
            token.ThrowIfCancellationRequested();
            EntriesInspected = snapshot.EntriesVisited;
            _estimator.Observe(EntriesInspected); _estimator.Saving();
            CanCancel = false;
            Status = "Saving scan results…";
            _diagnosticScan?.Stage(DiagnosticScanStage.Saving);
            Changed();
            // Once committing a completed scan, let the bounded atomic write finish.
            string? persistenceProblem = null;
            StorageScanSnapshot? previous = null;
            try { previous = await Task.Run(() => _store.Save(snapshot)); }
            catch (Exception error) { persistenceProblem = error.Message; _diagnosticScan?.ReadFailure(error); }
            if (_closed) return;
            _diagnosticScan?.Stage(DiagnosticScanStage.Presenting);
            Present(snapshot);
            EntriesInspected = snapshot.EntriesVisited;
            ScanVersion++;
            _diagnosticScan?.Stage(DiagnosticScanStage.Comparing);
            Growth = StorageScanComparison.Compare(snapshot, previous)
                .Select(e => new StorageGrowthRow(e.Path, e.Kind.ToString(), $"+{e.LogicalGrowthBytes / 1_048_576d:N2} MiB")).ToArray();
            BaselineStatus = persistenceProblem is not null
                ? $"Scan is visible but was not saved; growth unavailable: {persistenceProblem}"
                : previous is null || !string.Equals(previous.Root, snapshot.Root, StringComparison.OrdinalIgnoreCase)
                    ? "No prior scan for this root. This scan becomes the next baseline."
                    : $"Compared with {previous.FinishedUtc.ToLocalTime():G}. Positive logical-size changes only, for paths retained and fully measured in both scans. Missing paths have unknown growth.";
            _diagnosticScan?.End(DiagnosticScanStage.Completed, snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _diagnosticScan?.End(DiagnosticScanStage.Canceled);
            if (!_closed) Status = "Scan canceled. Previously displayed results and saved baseline were retained.";
        }
        catch (Exception error) { _diagnosticScan?.End(DiagnosticScanStage.Failed, error: error); if (!_closed) Status = $"Scan failed: {error.Message}. Previous results were retained."; }
        finally
        {
            _diagnosticScan?.End(DiagnosticScanStage.Canceled); _diagnosticScan = null;
            if (acquired) scanGate.Release();
            _cancellation.Dispose();
            _cancellation = null;
            IsIdle = true;
            IsScanning = false;
            CanCancel = false;
            if (!_closed) Changed();
        }
    }

    public void Cancel() { _diagnosticScan?.CancelRequested(); _cancellation?.Cancel(); }
    public void Close() { _closed = true; Cancel(); }
    public void RefreshAge()
    {
        if (!_closed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScanAge)));
        if (!_closed && IsScanning) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkEstimateText)));
    }

    private void Present(StorageScanSnapshot snapshot)
    {
        _lastFinishedUtc = snapshot.FinishedUtc;
        _lastSnapshot = snapshot;
        ResultsLabel = $"Results from {snapshot.Root}";
        HasCoverageWarning = !snapshot.CoverageComplete;
        EntryLimitReached = snapshot.EntryLimitReached;
        CoverageWarningTitle = snapshot.EntryLimitReached ? "SCAN STOPPED — ENTRY LIMIT REACHED" : "PARTIAL SCAN — COVERAGE INCOMPLETE";
        CoverageWarning = !HasCoverageWarning ? "" : snapshot.EntryLimitReached
            ? $"Stopped after {snapshot.EntriesVisited:N0} entries (limit {snapshot.EntryLimit:N0}) in {snapshot.Root}. " +
              "These are only the largest files FOUND, not the largest files on the whole drive or selected folder. Larger files may be missing. " +
              "Increase the scan entry limit below or select a smaller folder, then start a new scan."
            : $"Some files or folders in {snapshot.Root} could not be measured. These rankings are incomplete; larger files may be missing. Review coverage issues below.";
        Files = snapshot.Entries.Where(e => e.Kind == StorageScanEntryKind.File).Take(100).Select(Row).ToArray();
        Folders = snapshot.Entries.Where(e => e.Kind == StorageScanEntryKind.Folder).Take(100).Select(Row).ToArray();
        Issues = snapshot.Issues;
        Status = $"{(snapshot.CoverageComplete ? "Traversal completed" : "Partial scan")} · {snapshot.Root}\n" +
            $"As of {snapshot.FinishedUtc.ToLocalTime():G} · {snapshot.EntriesVisited:N0} entries inspected, " +
            $"{snapshot.FilesMeasured:N0} file paths measured, {snapshot.SkippedEntries:N0} skipped/failed, " +
            $"{snapshot.HardLinkAliases:N0} hard-link aliases. " +
            (snapshot.EntryLimitReached ? $"{snapshot.EntryLimit:N0}-entry limit reached. Increase the limit or select a smaller folder. " : "") +
            $"Largest 100 files and folders by allocated space shown; first {snapshot.Issues.Length} coverage issues listed. " +
            "Live-tree observations may change during scanning; this is not a point-in-time snapshot.";
    }

    private static StorageScanRow Row(StorageScanEntry e) => new(e.Path,
        e.LogicalBytes / 1_048_576d, e.AllocatedBytes / 1_048_576d,
        e.CoverageComplete ? "Measured" : "Partial");
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record StorageScanRow(string Path, double LogicalMiB, double AllocatedMiB, string Coverage);
internal sealed record StorageGrowthRow(string Path, string Kind, string Growth);
