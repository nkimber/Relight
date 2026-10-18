using System.IO.Enumeration;

namespace Relight.Core;

public sealed record CleanupRule(Guid Id, string Name, string Root, string Pattern,
    string[] Exclusions, int MinimumAgeDays = 14, int RetainNewest = 5,
    int MaximumFiles = 100, long MaximumBytes = 524_288_000, string? GuardExecutable = null)
{
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 ||
            string.IsNullOrWhiteSpace(Root) || !Path.IsPathFullyQualified(Root) ||
            Root.Length > 2000 || Exclusions is null || Exclusions.Length > 20 ||
            MinimumAgeDays is < 1 or > 3650 || RetainNewest is < 0 or > 1000 ||
            MaximumFiles is < 1 or > 200 || MaximumBytes is < 1 or > 10_737_418_240)
            throw new ArgumentException("Invalid cleanup rule: use an absolute folder, positive age, 1–200 files and at most 10 GiB per run.");
        if (!ValidPattern(Pattern) || Exclusions.Any(p => !ValidPattern(p)))
            throw new ArgumentException("Use filename patterns only (no paths), at most 100 characters.");
        string extension = Path.GetExtension(Pattern).ToLowerInvariant();
        if (!new[] { ".log", ".tmp", ".bak", ".old", ".dmp", ".cache", ".etl" }.Contains(extension))
            throw new ArgumentException("Reviewed cleanup supports .log, .tmp, .bak, .old, .dmp, .cache and .etl files only.");
        if (GuardExecutable is not null && (!Path.IsPathFullyQualified(GuardExecutable) ||
            !string.Equals(Path.GetExtension(GuardExecutable), ".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The optional application guard requires an absolute executable path.");
    }

    private static bool ValidPattern(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value.IndexOfAny(['\\', '/', ':', '\0', '<', '>', '|', '"']) < 0;

    public bool Matches(string name) => FileSystemName.MatchesSimpleExpression(Pattern, name, true) &&
        !Exclusions.Any(p => FileSystemName.MatchesSimpleExpression(p, name, true));
    public string Fingerprint() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this)));
}

public sealed record CleanupFile(string Path, string Identity, long Length, long AllocatedBytes,
    DateTimeOffset LastWriteUtc, long ChangeTime, uint Links, uint Attributes);
public sealed record CleanupIssue(string Path, string Reason, bool BlocksExecution = false);
public sealed record CleanupPreview(Guid Id, CleanupRule Rule, DateTimeOffset CreatedUtc,
    CleanupFile[] Candidates, CleanupIssue[] Issues, int IssueCount, bool Complete);
public sealed record CleanupItemOutcome(string Path, string Outcome, string? Detail = null);
public sealed record CleanupRun(Guid Id, Guid RuleId, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc,
    CleanupItemOutcome[] Items, long? AvailableBefore, long? AvailableAfter, string? Problem = null)
{
    public string Origin { get; init; } = "Reviewed";
    public CleanupRule? RuleSnapshot { get; init; }
}
public sealed record CleanupRuleBook(int SchemaVersion, long Revision, CleanupRule[] Rules)
{
    public bool AutomaticEnabled { get; init; }
    public AutomaticCleanupApproval[] AutomaticApprovals { get; init; } = [];
}
public sealed record AutomaticCleanupApproval(Guid Id, Guid RuleId, string RuleFingerprint,
    int IntervalHours, long TriggerBelowBytes, DateTimeOffset NextEligibleUtc);
public sealed record CleanupRunSummary(Guid Id, Guid RuleId, string Origin, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc, int Deleted, int Skipped, int NotCompleted, long? AvailableChangeBytes, string? Problem)
{
    public string? RuleName { get; init; }
}

public sealed record CleanupRunArchive(int SchemaVersion, CleanupRun[] Runs);
