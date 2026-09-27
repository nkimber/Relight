using System.Text.Json;

namespace Relight.Storage;

public sealed record EventHistoryQuery(
    Guid? ProfileId = null,
    EventSeverity? MinimumSeverity = null,
    OperationalEventKind? Kind = null,
    Guid? EpisodeId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ThroughUtc = null,
    int Limit = 500);

public sealed record EventHistoryResult(
    IReadOnlyList<OperationalEvent> Events,
    int TotalMatches,
    int SkippedMalformedLines)
{
    public bool Truncated => TotalMatches > Events.Count;
}

/// <summary>
/// Reads only files owned by the journal. A bounded latest-first result keeps the
/// dashboard responsive while reporting when matching older events were omitted.
/// </summary>
public sealed class OperationalEventHistoryReader(string dataDirectory)
{
    private readonly string _directory = Path.Combine(
        Path.GetFullPath(dataDirectory), "Logs");

    public async Task<EventHistoryResult> ReadAsync(EventHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 5000 ||
            query.FromUtc?.Offset is { } fromOffset && fromOffset != TimeSpan.Zero ||
            query.ThroughUtc?.Offset is { } throughOffset && throughOffset != TimeSpan.Zero ||
            query.FromUtc > query.ThroughUtc)
            throw new ArgumentException("History query is invalid.", nameof(query));
        if (!Directory.Exists(_directory)) return new([], 0, 0);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The log directory cannot be a reparse point.");

        var latest = new PriorityQueue<OperationalEvent, DateTimeOffset>();
        var seenIds = new HashSet<Guid>();
        int matched = 0;
        int malformed = 0;
        foreach (string path in Directory.EnumerateFiles(_directory,
                     "events-*.jsonl", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperationalEventJournal.OwnedFileName.IsMatch(Path.GetFileName(path))) continue;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous |
                        FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { continue; }
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                OperationalEvent? entry;
                try { entry = JsonSerializer.Deserialize<OperationalEvent>(line,
                    OperationalEventJournal.Json); }
                catch (JsonException) { malformed++; continue; }
                if (entry is null || entry.EventId == Guid.Empty ||
                    entry.OccurredUtc.Offset != TimeSpan.Zero ||
                    !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.Severity))
                {
                    malformed++;
                    continue;
                }
                if (!seenIds.Add(entry.EventId)) continue;
                if (query.ProfileId is { } profile && entry.ProfileId != profile ||
                    query.MinimumSeverity is { } severity && entry.Severity < severity ||
                    query.Kind is { } kind && entry.Kind != kind ||
                    query.EpisodeId is { } episode && entry.EpisodeId != episode ||
                    query.FromUtc is { } from && entry.OccurredUtc < from ||
                    query.ThroughUtc is { } through && entry.OccurredUtc > through)
                    continue;
                matched++;
                latest.Enqueue(entry, entry.OccurredUtc);
                if (latest.Count > query.Limit) latest.Dequeue();
            }
        }
        OperationalEvent[] rows = latest.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(entry => entry.OccurredUtc)
            .ThenBy(entry => entry.EventId)
            .ToArray();
        return new(rows, matched, malformed);
    }
}
