namespace Relight.Core;

public enum StorageScanEntryKind { File, Folder }

public sealed record StorageScanEntry(string Path, StorageScanEntryKind Kind,
    long LogicalBytes, long AllocatedBytes, bool CoverageComplete);

public sealed record StorageScanIssue(string Path, string Reason);

/// <summary>Bounded observations, not a filesystem snapshot or cleanup authorization.</summary>
public sealed record StorageScanSnapshot(int SchemaVersion, string Root,
    DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool TraversalComplete,
    int EntriesVisited, int FilesMeasured, int SkippedEntries, int HardLinkAliases,
    bool EntryLimitReached, StorageScanEntry[] Entries, StorageScanIssue[] Issues)
{
    public const int DefaultEntryLimit = 100_000;
    public const int MaximumEntryLimit = 10_000_000;
    public int EntryLimit { get; init; } = DefaultEntryLimit;
    public const int MaximumRetainedEntries = 2100;
    public const int MaximumIssues = 100;
    public bool CoverageComplete => TraversalComplete && SkippedEntries == 0 && !EntryLimitReached;
}

public sealed record StorageScanGrowth(string Path, StorageScanEntryKind Kind, long LogicalGrowthBytes);

public static class StorageScanComparison
{
    public static IReadOnlyList<StorageScanGrowth> Compare(StorageScanSnapshot current,
        StorageScanSnapshot? previous)
    {
        if (previous is null || !string.Equals(current.Root, previous.Root,
                StringComparison.OrdinalIgnoreCase)) return [];
        var baseline = previous.Entries.ToDictionary(e => (e.Kind, e.Path.ToUpperInvariant()));
        return current.Entries.Where(e => e.CoverageComplete && baseline.TryGetValue(
                (e.Kind, e.Path.ToUpperInvariant()), out var before) && before.CoverageComplete)
            .Select(e => new StorageScanGrowth(e.Path, e.Kind,
                e.LogicalBytes - baseline[(e.Kind, e.Path.ToUpperInvariant())].LogicalBytes))
            .Where(e => e.LogicalGrowthBytes > 0)
            .OrderByDescending(e => e.LogicalGrowthBytes).Take(100).ToArray();
    }
}
