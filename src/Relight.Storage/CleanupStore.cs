using System.Text.Json;
using System.Text.Json.Serialization;
using Relight.Core;

namespace Relight.Storage;

/// <summary>Revision-checked rules/approvals, latest detailed run and bounded summary history.</summary>
public sealed class CleanupStore(string dataDirectory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Storage");
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumDetailBytes = 4 * 1024 * 1024;
    public FileStream AcquireAutomaticWorkerLease()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "automatic-worker.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    public FileStream AcquireRunLease()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "cleanup-run.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public CleanupRuleBook LoadRules()
    {
        var book = Read<CleanupRuleBook>("cleanup-rules.json");
        if (book is null && File.Exists(Path.Combine(_directory, "cleanup-rules.json.bak")))
            throw new InvalidDataException("Cleanup rules are missing but a backup exists; preserve/recover the original rules before editing.");
        book ??= new(1, 0, []);
        if (book.SchemaVersion != 1 || book.Revision < 0 || book.Rules is null || book.Rules.Length > 50)
            throw new InvalidDataException("Cleanup rules are invalid or unsupported; the file was preserved.");
        var ids = new HashSet<Guid>();
        foreach (var rule in book.Rules)
        {
            if (rule is null || !ids.Add(rule.Id)) throw new InvalidDataException("Cleanup rule identities are invalid.");
            rule.Validate();
        }
        if (book.AutomaticApprovals is null || book.AutomaticApprovals.Length > 50 ||
            book.AutomaticApprovals.Select(a => a?.RuleId).Distinct().Count() != book.AutomaticApprovals.Length ||
            book.AutomaticApprovals.Select(a => a?.Id).Distinct().Count() != book.AutomaticApprovals.Length)
            throw new InvalidDataException("Automatic cleanup approvals are invalid.");
        foreach (var approval in book.AutomaticApprovals)
            if (approval is null || approval.Id == Guid.Empty || approval.NextEligibleUtc == default || approval.IntervalHours is < 1 or > 168 ||
                approval.TriggerBelowBytes is < 1_073_741_824 or > 1_099_511_627_776 ||
                !book.Rules.Any(r => r.Id == approval.RuleId && r.Fingerprint() == approval.RuleFingerprint))
                throw new InvalidDataException("Automatic cleanup approval no longer matches a saved rule.");
        return book;
    }

    public CleanupRuleBook SaveRules(long expectedRevision, CleanupRule[] rules)
    {
        using var lease = Lock();
        var current = LoadRules();
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Cleanup rules changed. Reopen the window before saving.");
        var next = new CleanupRuleBook(1, checked(current.Revision + 1), rules);
        if (rules.Length > 50 || rules.Select(r => r.Id).Distinct().Count() != rules.Length)
            throw new ArgumentException("Use at most 50 uniquely identified cleanup rules.");
        foreach (var rule in rules) rule.Validate();
        Write("cleanup-rules.json", next, backup: true);
        return next;
    }

    public CleanupRuleBook ApproveAutomatic(long expectedRevision, CleanupRule reviewedRule, int intervalHours, long triggerBelowBytes)
    {
        if (intervalHours is < 1 or > 168 || triggerBelowBytes is < 1_073_741_824 or > 1_099_511_627_776)
            throw new ArgumentException("Automatic cleanup requires a 1–168 hour interval and 1–1024 GiB trigger.");
        using var lease = Lock();
        var current = LoadRules();
        if (current.Revision != expectedRevision || !current.Rules.Any(r => r.Id == reviewedRule.Id && r.Fingerprint() == reviewedRule.Fingerprint()))
            throw new InvalidOperationException("Save the rule and generate a fresh preview before automatic approval.");
        var approval = new AutomaticCleanupApproval(Guid.NewGuid(), reviewedRule.Id, reviewedRule.Fingerprint(), intervalHours,
            triggerBelowBytes, _clock.GetUtcNow().AddHours(intervalHours));
        var next = current with { Revision = checked(current.Revision + 1), AutomaticEnabled = true,
            AutomaticApprovals = current.AutomaticApprovals.Where(a => a.RuleId != reviewedRule.Id).Append(approval).ToArray() };
        Write("cleanup-rules.json", next, true);
        return next;
    }

    public CleanupRuleBook DisableAutomatic()
    {
        using var lease = Lock();
        var current = LoadRules();
        var next = current with { Revision = checked(current.Revision + 1), AutomaticEnabled = false };
        Write("cleanup-rules.json", next, true); return next;
    }

    public CleanupRule? ReserveAutomatic(Guid approvalId)
    {
        using var lease = Lock();
        var current = LoadRules();
        var approval = current.AutomaticApprovals.SingleOrDefault(a => a.Id == approvalId);
        if (!current.AutomaticEnabled || approval is null || approval.NextEligibleUtc > _clock.GetUtcNow()) return null;
        var next = current with { Revision = checked(current.Revision + 1), AutomaticApprovals = current.AutomaticApprovals
            .Select(a => a.Id == approvalId ? a with { NextEligibleUtc = _clock.GetUtcNow().AddHours(a.IntervalHours) } : a).ToArray() };
        Write("cleanup-rules.json", next, true); // A failed/interrupted run consumes this interval.
        return current.Rules.Single(r => r.Id == approval.RuleId);
    }

    public IDisposable AcquireAutomaticAuthorization(Guid approvalId, string fingerprint)
    {
        var lease = Lock();
        try
        {
            var current = LoadRules();
            if (!current.AutomaticEnabled || !current.AutomaticApprovals.Any(a => a.Id == approvalId && a.RuleFingerprint == fingerprint))
                throw new InvalidOperationException("Automatic cleanup approval was revoked; remaining files were left alone.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public CleanupRun? LoadLastRun()
    {
        var run = Read<CleanupRun>("cleanup-last-run.json");
        if (run is not null) ValidateRun(run);
        return run;
    }

    private static void ValidateRun(CleanupRun run)
    {
        if (run.Id == Guid.Empty || run.RuleId == Guid.Empty || run.Items is null ||
            run.Origin is not "Reviewed" and not "Automatic" || run.StartedUtc == default ||
            run.Items.Length > 200 || run.Items.Any(i => i is null || string.IsNullOrWhiteSpace(i.Path) || string.IsNullOrWhiteSpace(i.Outcome)))
            throw new InvalidDataException("Cleanup run evidence is invalid; it was preserved.");
        if (run.RuleSnapshot is { } rule)
        {
            rule.Validate();
            if (rule.Id != run.RuleId) throw new InvalidDataException("Cleanup run rule identity is invalid.");
        }
    }

    public CleanupRun? LoadRun(Guid id)
    {
        var latest = LoadLastRun();
        return latest?.Id == id ? latest : LoadDetails().SingleOrDefault(r => r.Id == id);
    }

    private CleanupRun[] LoadDetails()
    {
        var archive = Read<CleanupRunArchive>("cleanup-run-details.json", MaximumDetailBytes);
        if (archive is null) return [];
        if (archive.SchemaVersion != 1 || archive.Runs is null || archive.Runs.Length > 100 ||
            archive.Runs.Any(r => r is null) || archive.Runs.Select(r => r.Id).Distinct().Count() != archive.Runs.Length)
            throw new InvalidDataException("Cleanup run details are invalid or unsupported; they were preserved.");
        foreach (var run in archive.Runs) ValidateRun(run);
        return archive.Runs;
    }
    public void SaveRun(CleanupRun run)
    {
        ValidateRun(run);
        if (JsonSerializer.SerializeToUtf8Bytes(run, Json).Length > MaximumBytes)
            throw new InvalidDataException("Cleanup run exceeds its size limit.");
        using var lease = Lock();
        // Refuse to overwrite unreadable evidence from a previous interrupted run.
        var previous = LoadLastRun();
        var history = LoadHistory();
        var details = LoadDetails();
        // Archive final results, and retain unfinished/legacy last evidence before a new run replaces it.
        // Pending per-file updates remain in the latest file, avoiding large archive rewrites per dispatch.
        var additions = new List<CleanupRun>();
        if (previous is not null && previous.Id != run.Id &&
            (previous.FinishedUtc is null || !details.Any(r => r.Id == previous.Id))) additions.Add(previous);
        if (run.FinishedUtc is not null) additions.Add(run);
        if (additions.Count > 0)
        {
            var retained = details.Where(r => !additions.Any(a => a.Id == r.Id)).Concat(additions).TakeLast(100).ToArray();
            while (JsonSerializer.SerializeToUtf8Bytes(new CleanupRunArchive(1, retained), Json).Length > MaximumDetailBytes && retained.Length > 1)
                retained = retained[1..];
            Write("cleanup-run-details.json", new CleanupRunArchive(1, retained), false, MaximumDetailBytes);
        }
        Write("cleanup-last-run.json", run, backup: false);
        var summary = new CleanupRunSummary(run.Id, run.RuleId, run.Origin, run.StartedUtc, run.FinishedUtc,
            run.Items.Count(i => i.Outcome == "Deleted permanently"), run.Items.Count(i => i.Outcome == "Skipped"),
            run.Items.Count(i => i.Outcome is not "Deleted permanently" and not "Skipped"),
            run.AvailableBefore is { } before && run.AvailableAfter is { } after ? after - before : null,
            run.Problem is { Length: > 500 } problem ? problem[..500] : run.Problem) { RuleName = run.RuleSnapshot?.Name };
        Write("cleanup-history.json", history.Where(h => h.Id != run.Id).Append(summary).TakeLast(100).ToArray(), false);
    }

    public CleanupRunSummary[] LoadHistory()
    {
        var history = Read<CleanupRunSummary[]>("cleanup-history.json") ?? [];
        if (history.Length > 100 || history.Any(h => h is null || h.Id == Guid.Empty || h.RuleId == Guid.Empty ||
            h.Deleted < 0 || h.Skipped < 0 || h.NotCompleted < 0))
            throw new InvalidDataException("Cleanup history is invalid; it was preserved.");
        return history;
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(_directory);
        return new FileStream(Path.Combine(_directory, "cleanup-store.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private T? Read<T>(string name, int maximumBytes = MaximumBytes) where T : class
    {
        string path = Path.Combine(_directory, name);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > maximumBytes) throw new InvalidDataException("Cleanup data exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Cleanup data is empty.");
    }

    private void Write<T>(string name, T value, bool backup, int maximumBytes = MaximumBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Cleanup data exceeds its size limit.");
        string path = Path.Combine(_directory, name), temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null);
            else File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
