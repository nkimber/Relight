using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Relight.Core;
using Relight.Storage;

namespace Relight.ViewModels;

internal sealed class CleanupHistoryViewModel(string dataDirectory) : INotifyPropertyChanged
{
    private readonly CleanupStore _store = new(dataDirectory);
    private CleanupHistoryRow[] _all = [];
    private CleanupHistoryRow? _selected;
    private string _origin = "All runs";
    private bool _problemsOnly, _closed;
    private int _selectionVersion;
    public string[] Origins { get; } = ["All runs", "Reviewed", "Automatic"];
    public string Origin { get => _origin; set { _origin = value; Filter(); } }
    public bool ProblemsOnly { get => _problemsOnly; set { _problemsOnly = value; Filter(); } }
    public IReadOnlyList<CleanupHistoryRow> Runs { get; private set; } = [];
    public CleanupHistoryRow? SelectedRun
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value; _selectionVersion++; Items = [];
            Details = value is null ? "Select a run to inspect its saved outcomes." : "Loading saved run details…";
            Changed();
        }
    }
    public IReadOnlyList<CleanupItemOutcome> Items { get; private set; } = [];
    public string Details { get; private set; } = "Select a run to inspect its saved outcomes.";
    public string Status { get; private set; } = "Loading cleanup history…";
    public bool IsIdle { get; private set; } = true;

    public async Task RefreshAsync()
    {
        if (!IsIdle || _closed) return;
        IsIdle = false; Changed();
        var selectedId = SelectedRun?.Run.Id;
        try
        {
            var history = await Task.Run(() => _store.LoadHistory());
            if (_closed) return;
            _all = history.OrderByDescending(r => r.StartedUtc).Select(r => new CleanupHistoryRow(r)).ToArray();
            Filter();
            SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == selectedId) ?? Runs.FirstOrDefault();
            Status = $"{_all.Length} retained run summaries. Refreshed {DateTimeOffset.Now:G}. Refresh to see new runs. " +
                "Details are retained for up to 100 runs / 4 MiB; older or legacy summaries may have no file details.";
            await LoadSelectedAsync();
        }
        catch (Exception error)
        {
            _all = []; Runs = []; SelectedRun = null; Items = [];
            Details = "Run details unavailable.";
            Status = $"Cleanup history unavailable: {error.Message}. Original evidence was preserved.";
        }
        finally { IsIdle = true; Changed(); }
    }

    private void Filter()
    {
        Runs = _all.Where(r => (Origin == "All runs" || r.Run.Origin == Origin) &&
            (!ProblemsOnly || r.Run.Problem is not null || r.Run.FinishedUtc is null || r.Run.NotCompleted > 0 || r.Run.Skipped > 0)).ToArray();
        if (_selected is not null && !Runs.Contains(_selected)) SelectedRun = null;
        Changed();
    }

    public async Task LoadSelectedAsync()
    {
        int version = ++_selectionVersion;
        var selected = SelectedRun;
        Items = []; Details = selected is null ? "Select a run to inspect its saved outcomes." : "Loading saved run details…";
        Changed();
        if (selected is null || _closed) return;
        try
        {
            var run = await Task.Run(() => _store.LoadRun(selected.Run.Id));
            if (_closed || version != _selectionVersion) return;
            if (run is null)
            {
                Details = $"Run {selected.Run.Id}: file details were not retained (legacy summary or detail retention limit). " +
                    "The summary remains available. Missing details do not mean no files were affected.";
            }
            else
            {
                Items = run.Items;
                string timing = run.FinishedUtc is { } finished ? $"Finished: {finished.ToLocalTime():G} (UTC {finished:O})." :
                    "No final outcome recorded: this run may be in progress or interrupted. Pending outcomes are unknown and will not be replayed.";
                string capacity = run.AvailableBefore is { } before && run.AvailableAfter is { } after
                    ? $"Available before: {before:N0} bytes; after: {after:N0} bytes. Observed change: {after - before:N0} bytes; other disk activity can affect this."
                    : "Observed free-space change is unknown (one or both capacity readings unavailable).";
                string rule = run.RuleSnapshot is { } snapshot
                    ? $"Saved rule at execution: {snapshot.Name}\nFolder: {snapshot.Root}\nPattern: {snapshot.Pattern}; exclusions: {string.Join(';', snapshot.Exclusions)}; " +
                        $"minimum age {snapshot.MinimumAgeDays} days; keep newest {snapshot.RetainNewest}; " +
                        $"cap {snapshot.MaximumFiles} files / {snapshot.MaximumBytes:N0} logical bytes; guard: {snapshot.GuardExecutable ?? "none"}."
                    : "Rule settings at execution were not recorded by this older version. Current rule settings may differ.";
                Details = $"Run {run.Id} · {run.Origin}\nRule ID: {run.RuleId}\nStarted: {run.StartedUtc.ToLocalTime():G} (UTC {run.StartedUtc:O}).\n" +
                    $"{timing}\n{rule}\n{capacity}\n{run.Problem}";
            }
        }
        catch (Exception error)
        {
            if (_closed || version != _selectionVersion) return;
            Details = $"Run details unavailable: {error.Message}. Original evidence was preserved.";
        }
        Changed();
    }

    public void Close() { _closed = true; _selectionVersion++; }
    private void Changed() { if (!_closed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class CleanupHistoryRow(CleanupRunSummary run)
{
    public CleanupRunSummary Run => run;
    public string Started => run.StartedUtc.ToLocalTime().ToString("G");
    public string Rule => run.RuleName ?? run.RuleId.ToString();
    public string Origin => run.Origin;
    public int Deleted => run.Deleted;
    public int Skipped => run.Skipped;
    public int UnknownOrNotAttempted => run.NotCompleted;
    public string Result => run.FinishedUtc is null ? "In progress / interrupted; pending unknown" :
        run.Problem is not null ? "Finished with problem" : run.Skipped > 0 || run.NotCompleted > 0 ? "Finished with skips / uncompleted files" : "Finished";
    public string AvailableChange => run.AvailableChangeBytes is { } delta ? $"{delta / 1_048_576d:N2} MiB" : "Unknown";
}
