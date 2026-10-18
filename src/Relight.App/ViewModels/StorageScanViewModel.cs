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

internal sealed class StorageScanViewModel(string dataDirectory, SemaphoreSlim scanGate) : INotifyPropertyChanged
{
    private readonly StorageScanStore _store = new(dataDirectory);
    private CancellationTokenSource? _cancellation;
    private bool _closed;
    private DateTimeOffset? _lastFinishedUtc;
    public string Root { get; set; } = DiskSpaceMonitor.SystemDrive;
    public bool IsIdle { get; private set; } = true;
    public bool IsScanning => !IsIdle;
    public bool CanCancel { get; private set; }
    public string Status { get; private set; } = "Choose a local folder or drive and select Scan folder. No files will be deleted.";
    public string BaselineStatus { get; private set; } = "No growth comparison yet.";
    public string ScanAge => _lastFinishedUtc is { } time
        ? $"Latest completed scan: {time.ToLocalTime():G} · age {Math.Max(0, (DateTimeOffset.UtcNow - time).TotalMinutes):N0} minutes"
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
        IsIdle = false;
        CanCancel = true;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        Status = "Waiting for the scan worker…";
        Changed();
        var progress = new Progress<int>(count =>
        {
            if (_closed || !CanCancel || _cancellation?.Token != token || token.IsCancellationRequested) return;
            Status = $"Scanning {root} · {count:N0} entries inspected…";
            Changed();
        });
        bool acquired = false;
        try
        {
            await scanGate.WaitAsync(token);
            acquired = true;
            var snapshot = await Task.Run(() => new StorageScanner().Scan(root, token, progress), token);
            token.ThrowIfCancellationRequested();
            CanCancel = false;
            Status = "Saving completed scan…";
            Changed();
            // Once committing a completed scan, let the bounded atomic write finish.
            string? persistenceProblem = null;
            StorageScanSnapshot? previous = null;
            try { previous = await Task.Run(() => _store.Save(snapshot)); }
            catch (Exception error) { persistenceProblem = error.Message; }
            if (_closed) return;
            Present(snapshot);
            Growth = StorageScanComparison.Compare(snapshot, previous)
                .Select(e => new StorageGrowthRow(e.Path, e.Kind.ToString(), $"+{e.LogicalGrowthBytes / 1_048_576d:N2} MiB")).ToArray();
            BaselineStatus = persistenceProblem is not null
                ? $"Scan is visible but was not saved; growth unavailable: {persistenceProblem}"
                : previous is null || !string.Equals(previous.Root, snapshot.Root, StringComparison.OrdinalIgnoreCase)
                    ? "No prior scan for this root. This scan becomes the next baseline."
                    : $"Compared with {previous.FinishedUtc.ToLocalTime():G}. Positive logical-size changes only, for paths retained and fully measured in both scans. Missing paths have unknown growth.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_closed) Status = "Scan canceled. Previously displayed results and saved baseline were retained.";
        }
        catch (Exception error) { if (!_closed) Status = $"Scan failed: {error.Message}. Previous results were retained."; }
        finally
        {
            if (acquired) scanGate.Release();
            _cancellation.Dispose();
            _cancellation = null;
            IsIdle = true;
            CanCancel = false;
            if (!_closed) Changed();
        }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void Close() { _closed = true; Cancel(); }
    public void RefreshAge()
    {
        if (!_closed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScanAge)));
    }

    private void Present(StorageScanSnapshot snapshot)
    {
        _lastFinishedUtc = snapshot.FinishedUtc;
        Files = snapshot.Entries.Where(e => e.Kind == StorageScanEntryKind.File).Take(100).Select(Row).ToArray();
        Folders = snapshot.Entries.Where(e => e.Kind == StorageScanEntryKind.Folder).Take(100).Select(Row).ToArray();
        Issues = snapshot.Issues;
        Status = $"{(snapshot.CoverageComplete ? "Traversal completed" : "Partial scan")} · {snapshot.Root}\n" +
            $"As of {snapshot.FinishedUtc.ToLocalTime():G} · {snapshot.EntriesVisited:N0} entries inspected, " +
            $"{snapshot.FilesMeasured:N0} file paths measured, {snapshot.SkippedEntries:N0} skipped/failed, " +
            $"{snapshot.HardLinkAliases:N0} hard-link aliases. " +
            (snapshot.EntryLimitReached ? "100,000-entry limit reached. Select a smaller folder for fuller coverage. " : "") +
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
