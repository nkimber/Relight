using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.ViewModels;

internal sealed class ReviewedCleanupViewModel : INotifyPropertyChanged
{
    private readonly CleanupStore _store;
    private readonly ReviewedCleanupService _service;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _cancel = new();
    private CleanupRuleBook _book = new(1, 0, []);
    private CleanupPreview? _preview;
    private bool _closed;
    public CleanupEditor Editor { get; private set; } = new();
    public IReadOnlyList<CleanupRule> Rules => _book.Rules;
    public CleanupRule? SelectedRule { get; set; }
    public bool IsIdle { get; private set; }
    public bool CanDelete => IsIdle && _preview is { Complete: true } && Candidates.Count > 0;
    public string Status { get; private set; } = "Loading reviewed cleanup rules…";
    public IReadOnlyList<CleanupCandidateRow> Candidates { get; private set; } = [];
    public IReadOnlyList<CleanupIssue> Issues { get; private set; } = [];
    public IReadOnlyList<CleanupItemOutcome> Outcomes { get; private set; } = [];

    public ReviewedCleanupViewModel(string dataDirectory, SemaphoreSlim gate)
    {
        _store = new(dataDirectory); _service = new(dataDirectory); _gate = gate;
        WatchEditor();
    }

    private void WatchEditor() => Editor.PropertyChanged += (_, _) => Invalidate();
    private void Invalidate()
    {
        _preview = null; _service.InvalidatePreview();
        if (IsIdle) Status = "Rule changed. Generate a new preview before cleanup.";
        Changed();
    }

    public async Task LoadAsync()
    {
        try
        {
            _book = await Task.Run(() => _store.LoadRules());
            var last = await Task.Run(() => _store.LoadLastRun());
            Outcomes = last?.Items ?? [];
            Status = last is null ? "Automatic deletion is disabled. Save or preview a reviewed rule."
                : $"Last run {last.Id}: {(last.FinishedUtc is null ? "interrupted; pending outcomes are unknown" : "finished")} " +
                  $"{last.Problem}";
        }
        catch (Exception error) { Status = $"Cleanup data unavailable: {error.Message}. Original files were preserved."; }
        finally { IsIdle = true; Changed(); }
    }

    public void NewRule()
    {
        if (!IsIdle) return;
        Editor = new(); SelectedRule = null; WatchEditor(); Invalidate();
    }
    public void EditSelected()
    {
        if (!IsIdle || SelectedRule is null) return;
        Editor = new(SelectedRule); WatchEditor(); Invalidate();
    }

    public async Task SaveAsync() => await WorkAsync(async () =>
    {
        var rule = Editor.Build();
        await Task.Run(() => _service.ValidateRoot(rule));
        _book = await Task.Run(() => _store.SaveRules(_book.Revision,
            _book.Rules.Where(r => r.Id != rule.Id).Append(rule).ToArray()));
        Invalidate();
        Status = "Reviewed rule saved. Saving never deletes files or enables automatic cleanup.";
    });

    public async Task RemoveAsync() => await WorkAsync(async () =>
    {
        if (SelectedRule is null) throw new ArgumentException("Select a saved rule first.");
        _book = await Task.Run(() => _store.SaveRules(_book.Revision, _book.Rules.Where(r => r.Id != SelectedRule.Id).ToArray()));
        SelectedRule = null; Invalidate(); Status = "Saved rule removed. No files were deleted.";
    });

    public async Task PreviewAsync() => await WorkAsync(async () =>
    {
        _preview = null;
        var rule = Editor.Build();
        await _gate.WaitAsync(_cancel.Token);
        try { _preview = await Task.Run(() => _service.PreviewAsync(rule, _cancel.Token)); }
        finally { _gate.Release(); }
        Candidates = _preview.Candidates.Select(f => new CleanupCandidateRow(f)).ToArray();
        Issues = _preview.Issues;
        Status = $"Preview {_preview.CreatedUtc.ToLocalTime():G}: {_preview.Candidates.Length} eligible files; " +
            $"estimated allocation {_preview.Candidates.Sum(f => f.AllocatedBytes) / 1_048_576d:N2} MiB. " +
            $"{_preview.IssueCount} exclusions/issues. " +
            (_preview.Complete ? "Select files explicitly. Preview expires after 10 minutes." : "Coverage is incomplete; deletion is blocked. Fix issues and preview again.") +
            $" Rule: {rule.Pattern}, age ≥ {rule.MinimumAgeDays} days, keep newest {rule.RetainNewest}; immediate files only. Permanent deletion has no undo.";
    });

    public string Confirmation()
    {
        if (!CanDelete) throw new InvalidOperationException("Generate a complete preview first.");
        var selected = Candidates.Where(c => c.Selected).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Select the individual files to delete.");
        return $"Permanently delete {selected.Length} selected file(s) from:\n{_preview!.Rule.Root}\n\n" +
            $"Estimated allocation: {selected.Sum(c => c.File.AllocatedBytes) / 1_048_576d:N2} MiB.\n" +
            "This bypasses the Recycle Bin and cannot be undone. Each file will be reverified; changed or locked files will be skipped. Automatic deletion stays disabled.";
    }

    public async Task DeleteAsync() => await WorkAsync(async () =>
    {
        var preview = _preview ?? throw new InvalidOperationException("Generate a fresh preview.");
        string[] selected = Candidates.Where(c => c.Selected).Select(c => c.File.Path).ToArray();
        _preview = null;
        await _gate.WaitAsync(_cancel.Token);
        try
        {
            var run = await Task.Run(() => _service.ExecuteReviewedAsync(preview.Id, selected, _cancel.Token));
            Outcomes = run.Items;
            string delta = run.AvailableBefore is { } before && run.AvailableAfter is { } after
                ? $"Observed free-space change: {(after - before) / 1_048_576d:N2} MiB (other activity can affect this)."
                : "Free-space change could not be measured.";
            Status = $"Run {run.Id}: {run.Items.Count(i => i.Outcome == "Deleted permanently")} deleted; " +
                $"{run.Items.Count(i => i.Outcome == "Skipped")} skipped. {delta} {run.Problem} Generate a new preview before any further cleanup.";
        }
        finally { _gate.Release(); }
    });

    private async Task WorkAsync(Func<Task> work)
    {
        if (!IsIdle || _closed) return;
        IsIdle = false; Status = "Working…"; Changed();
        try { await work(); }
        catch (Exception error)
        {
            _preview = null; _service.InvalidatePreview();
            Status = $"Cleanup stopped: {error.Message}. Review the latest run below before retrying; any interrupted pending outcome is unknown.";
            try { Outcomes = (await Task.Run(() => _store.LoadLastRun()))?.Items ?? Outcomes; }
            catch (Exception evidence) { Status += $" Run evidence unavailable: {evidence.Message}"; }
        }
        finally { IsIdle = true; if (!_closed) Changed(); }
    }
    public void Close() { _closed = true; _cancel.Cancel(); _service.InvalidatePreview(); }
    private void Changed() { if (!_closed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class CleanupCandidateRow(CleanupFile file)
{
    public bool Selected { get; set; }
    public CleanupFile File => file;
    public double LogicalMiB => file.Length / 1_048_576d;
    public double AllocatedMiB => file.AllocatedBytes / 1_048_576d;
    public string Modified => file.LastWriteUtc.ToLocalTime().ToString("G");
}

internal sealed class CleanupEditor : INotifyPropertyChanged
{
    private readonly Guid _id;
    private string _name = "Old logs", _root = System.IO.Path.GetTempPath().TrimEnd('\\'), _pattern = "*.log", _exclusions = "",
        _age = "14", _keep = "5", _files = "100", _mib = "500", _guard = "";
    public CleanupEditor(CleanupRule? rule = null)
    {
        _id = rule?.Id ?? Guid.NewGuid();
        if (rule is null) return;
        _name = rule.Name; _root = rule.Root; _pattern = rule.Pattern; _exclusions = string.Join(";", rule.Exclusions);
        _age = rule.MinimumAgeDays.ToString(); _keep = rule.RetainNewest.ToString(); _files = rule.MaximumFiles.ToString();
        _mib = (rule.MaximumBytes / 1_048_576).ToString(); _guard = rule.GuardExecutable ?? "";
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Root { get => _root; set => Set(ref _root, value); }
    public string Pattern { get => _pattern; set => Set(ref _pattern, value); }
    public string Exclusions { get => _exclusions; set => Set(ref _exclusions, value); }
    public string Age { get => _age; set => Set(ref _age, value); }
    public string Keep { get => _keep; set => Set(ref _keep, value); }
    public string Files { get => _files; set => Set(ref _files, value); }
    public string MiB { get => _mib; set => Set(ref _mib, value); }
    public string Guard { get => _guard; set => Set(ref _guard, value); }
    public CleanupRule Build()
    {
        var rule = new CleanupRule(_id, Name.Trim(), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(Root)), Pattern.Trim(),
            Exclusions.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            int.Parse(Age, CultureInfo.InvariantCulture), int.Parse(Keep, CultureInfo.InvariantCulture),
            int.Parse(Files, CultureInfo.InvariantCulture), checked(long.Parse(MiB, CultureInfo.InvariantCulture) * 1_048_576),
            string.IsNullOrWhiteSpace(Guard) ? null : Guard.Trim());
        if (!System.IO.Path.IsPathFullyQualified(Root)) throw new ArgumentException("Use an absolute cleanup folder path.");
        rule.Validate(); return rule;
    }
    private void Set(ref string field, string value) { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(null)); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
