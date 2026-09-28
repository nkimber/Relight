using System.Text.Json;
using System.Text;

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

public sealed record EventHistorySummary(
    int ObservedDisappearances,
    int AutomaticAttemptsReserved,
    int AutomaticLaunchesDispatched,
    int StableAutomaticRecoveries,
    int OtherStableStarts,
    int Lockouts,
    int MonitoringGaps,
    int MonitoringRestorations,
    TimeSpan PairedMonitoringGapTimestampSpan,
    int UnpairedMonitoringTransitions);

public sealed record EventHistoryProfile(Guid Id, string? Name);

public sealed record EventHistoryOverview(EventHistoryResult Results,
    EventHistorySummary Summary, IReadOnlyList<EventHistoryProfile> Profiles);

public sealed record DashboardEventMilestones(DateTimeOffset? LastOutageUtc,
    DateTimeOffset? LastAutomaticRecoveryUtc);
public sealed record DashboardEventHistory(
    IReadOnlyDictionary<Guid, DashboardEventMilestones> Profiles, int SkippedMalformedLines);

public enum EventHistoryExportFormat { Text, Csv }
public sealed record EventHistoryExportResult(int ExportedEvents, int SkippedMalformedLines);

/// <summary>
/// Reads only files owned by the journal. A bounded latest-first result keeps the
/// dashboard responsive while reporting when matching older events were omitted.
/// </summary>
public sealed class OperationalEventHistoryReader(string dataDirectory)
{
    private readonly string _dataDirectory = Path.GetFullPath(dataDirectory);
    private readonly string _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs");

    public async Task<DashboardEventHistory> ReadDashboardMilestonesAsync(
        CancellationToken cancellationToken = default)
    {
        var milestones = new Dictionary<Guid, DashboardEventMilestones>();
        ScanSummary result = await ScanAsync(new EventHistoryQuery(), entry =>
        {
            if (entry.ProfileId is not { } id) return Task.CompletedTask;
            if (entry.Kind != OperationalEventKind.TargetDisappeared &&
                !(entry.Kind == OperationalEventKind.ObservationCompleted &&
                  entry.Origin == Relight.Core.ObservationOrigin.AutomaticLaunch))
                return Task.CompletedTask;
            milestones.TryGetValue(id, out DashboardEventMilestones? prior);
            prior ??= new(null, null);
            milestones[id] = entry.Kind == OperationalEventKind.TargetDisappeared
                ? prior with { LastOutageUtc = Later(prior.LastOutageUtc, entry.OccurredUtc) }
                : prior with { LastAutomaticRecoveryUtc = Later(
                    prior.LastAutomaticRecoveryUtc, entry.OccurredUtc) };
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return new(milestones, result.Malformed);
    }

    private static DateTimeOffset Later(DateTimeOffset? previous, DateTimeOffset candidate)
        => previous is { } time && time > candidate ? time : candidate;

    public async Task<EventHistoryResult> ReadAsync(EventHistoryQuery query,
        CancellationToken cancellationToken = default)
        => (await ReadOverviewAsync(query, cancellationToken).ConfigureAwait(false)).Results;

    public async Task<EventHistoryOverview> ReadOverviewAsync(EventHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(query);
        var latest = new PriorityQueue<OperationalEvent, DateTimeOffset>();
        int disappearances = 0;
        int reservations = 0;
        int dispatches = 0;
        int automaticRecoveries = 0;
        int otherStableStarts = 0;
        int lockouts = 0;
        int gaps = 0;
        int restorations = 0;
        var monitoringTransitions = new List<(Guid? ProfileId, DateTimeOffset OccurredUtc,
            Guid EventId, OperationalEventKind Kind)>();
        var profiles = new Dictionary<Guid, (string? Name, DateTimeOffset NamedAt)>();
        ScanSummary summary = await ScanAsync(query, entry =>
        {
            latest.Enqueue(entry, entry.OccurredUtc);
            if (latest.Count > query.Limit) latest.Dequeue();
            return Task.CompletedTask;
        }, cancellationToken, entry =>
        {
            switch (entry.Kind)
            {
                case OperationalEventKind.TargetDisappeared: disappearances++; break;
                case OperationalEventKind.LaunchReserved: reservations++; break;
                case OperationalEventKind.LaunchDispatched: dispatches++; break;
                case OperationalEventKind.ObservationCompleted:
                    if (entry.Origin == Relight.Core.ObservationOrigin.AutomaticLaunch)
                        automaticRecoveries++;
                    else if (entry.Origin is Relight.Core.ObservationOrigin.ExplicitStart or
                             Relight.Core.ObservationOrigin.ExternalStart)
                        otherStableStarts++;
                    break;
                case OperationalEventKind.LockoutEntered: lockouts++; break;
                case OperationalEventKind.MonitoringGap:
                    gaps++;
                    break;
                case OperationalEventKind.MonitoringRestored:
                    restorations++;
                    break;
            }
        }, entry =>
        {
            if ((entry.Kind is OperationalEventKind.MonitoringGap or
                    OperationalEventKind.MonitoringRestored) &&
                (query.ProfileId is null || entry.ProfileId == query.ProfileId) &&
                (query.EpisodeId is null || entry.EpisodeId == query.EpisodeId))
                monitoringTransitions.Add((entry.ProfileId, entry.OccurredUtc,
                    entry.EventId, entry.Kind));
            if (entry.ProfileId is not { } id) return;
            string? name = string.IsNullOrWhiteSpace(entry.ProfileName)
                ? null : entry.ProfileName;
            if (!profiles.TryGetValue(id, out var existing))
                profiles.Add(id, (name, name is null ? DateTimeOffset.MinValue : entry.OccurredUtc));
            else if (name is not null && entry.OccurredUtc >= existing.NamedAt)
                profiles[id] = (name, entry.OccurredUtc);
        }).ConfigureAwait(false);
        OperationalEvent[] rows = latest.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(entry => entry.OccurredUtc)
            .ThenBy(entry => entry.EventId)
            .ToArray();
        (TimeSpan pairedTimestampSpan, int unpairedTransitions) =
            SumPairedMonitoringTimestampSpans(monitoringTransitions,
                query.FromUtc, query.ThroughUtc);
        return new(new(rows, summary.Matched, summary.Malformed),
            new(disappearances, reservations, dispatches, automaticRecoveries,
                otherStableStarts, lockouts, gaps, restorations,
                pairedTimestampSpan, unpairedTransitions),
            profiles.Select(item => new EventHistoryProfile(item.Key, item.Value.Name))
                .ToArray());
    }

    private static (TimeSpan PairedTimestampSpan, int UnpairedTransitions)
        SumPairedMonitoringTimestampSpans(List<(Guid? ProfileId, DateTimeOffset OccurredUtc,
            Guid EventId, OperationalEventKind Kind)> transitions,
            DateTimeOffset? fromUtc, DateTimeOffset? throughUtc)
    {
        // File rotation and concurrent writers do not guarantee scan order.
        // Pair retained events before clipping complete intervals to the selected
        // period. A missing or ambiguous endpoint never earns a duration.
        var open = new Dictionary<Guid, (DateTimeOffset StartedUtc, bool Ambiguous)>();
        TimeSpan known = TimeSpan.Zero;
        int unpaired = 0;
        foreach (var entry in transitions.OrderBy(item => item.OccurredUtc)
                     .ThenBy(item => item.EventId))
        {
            if (entry.ProfileId is not { } profileId)
            {
                if (InRange(entry.OccurredUtc, fromUtc, throughUtc)) unpaired++;
                continue;
            }
            if (entry.Kind == OperationalEventKind.MonitoringGap)
            {
                if (open.ContainsKey(profileId))
                {
                    var previous = open[profileId];
                    open[profileId] = (previous.StartedUtc, true);
                    if (InRange(entry.OccurredUtc, fromUtc, throughUtc)) unpaired++;
                }
                else open.Add(profileId, (entry.OccurredUtc, false));
            }
            else if (open.Remove(profileId, out var gap))
            {
                if (!gap.Ambiguous && entry.OccurredUtc >= gap.StartedUtc)
                {
                    DateTimeOffset start = fromUtc is { } from && from > gap.StartedUtc
                        ? from : gap.StartedUtc;
                    DateTimeOffset end = throughUtc is { } through &&
                        through < entry.OccurredUtc ? through : entry.OccurredUtc;
                    if (end > start) known += end - start;
                }
                else if (Overlaps(gap.StartedUtc, entry.OccurredUtc,
                             fromUtc, throughUtc)) unpaired++;
            }
            else if (InRange(entry.OccurredUtc, fromUtc, throughUtc)) unpaired++;
        }
        return (known, unpaired + open.Values.Count(gap =>
            throughUtc is null || gap.StartedUtc <= throughUtc));
    }

    private static bool InRange(DateTimeOffset timestamp, DateTimeOffset? fromUtc,
        DateTimeOffset? throughUtc) =>
        (fromUtc is null || timestamp >= fromUtc) &&
        (throughUtc is null || timestamp <= throughUtc);

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset? fromUtc, DateTimeOffset? throughUtc) =>
        (throughUtc is null || start < throughUtc) &&
        (fromUtc is null || end > fromUtc);

    public async Task<EventHistoryExportResult> ExportAsync(EventHistoryQuery query,
        string destination, EventHistoryExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ValidateQuery(query);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        // An episode export is a diagnostic chain. Row-view filters must not
        // silently omit its earlier events or other transition kinds.
        EventHistoryQuery exportQuery = query.EpisodeId is { } episodeId
            ? new(EpisodeId: episodeId, Limit: query.Limit)
            : query;
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Choose an export destination.", nameof(destination));
        string output = Path.GetFullPath(destination);
        string relative = Path.GetRelativePath(_dataDirectory, output);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new ArgumentException("Choose a destination outside Relight's data directory.",
                nameof(destination));
        string parent = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The export folder does not exist.");
        string temporary = Path.Combine(parent, $".relight-export-{Guid.NewGuid():N}.tmp");
        try
        {
            ScanSummary summary;
            {
                await using var stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                if (format == EventHistoryExportFormat.Csv)
                    await writer.WriteLineAsync(CsvHeader.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                summary = await ScanAsync(exportQuery, entry =>
                    writer.WriteLineAsync((format == EventHistoryExportFormat.Csv
                        ? CsvLine(entry with { ProcessIdentity = null })
                        : TextBlock(entry with { ProcessIdentity = null })).AsMemory(),
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
            return new(summary.Matched, summary.Malformed);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record ScanSummary(int Matched, int Malformed);

    private async Task<ScanSummary> ScanAsync(EventHistoryQuery query,
        Func<OperationalEvent, Task> visit, CancellationToken cancellationToken,
        Action<OperationalEvent>? beforeSeverityAndKind = null,
        Action<OperationalEvent>? beforeFilters = null)
    {
        if (!Directory.Exists(_directory)) return new(0, 0);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The log directory cannot be a reparse point.");
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
                beforeFilters?.Invoke(entry);
                if (query.ProfileId is { } profile && entry.ProfileId != profile ||
                    query.EpisodeId is { } episode && entry.EpisodeId != episode ||
                    query.FromUtc is { } from && entry.OccurredUtc < from ||
                    query.ThroughUtc is { } through && entry.OccurredUtc > through)
                    continue;
                beforeSeverityAndKind?.Invoke(entry);
                if (query.MinimumSeverity is { } severity && entry.Severity < severity ||
                    query.Kind is { } kind && entry.Kind != kind)
                    continue;
                matched++;
                await visit(entry).ConfigureAwait(false);
            }
        }
        return new(matched, malformed);
    }

    private static void ValidateQuery(EventHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 5000 ||
            query.FromUtc?.Offset is { } fromOffset && fromOffset != TimeSpan.Zero ||
            query.ThroughUtc?.Offset is { } throughOffset && throughOffset != TimeSpan.Zero ||
            query.FromUtc > query.ThroughUtc)
            throw new ArgumentException("History query is invalid.", nameof(query));
    }

    private const string CsvHeader = "occurredUtc,eventId,severity,kind,profileId,profileName,episodeId,operationId,previousState,newState,origin,attemptNumber,attemptLimit,processIdentity,nativeErrorCode,failureCategory";

    private static string CsvLine(OperationalEvent entry) => string.Join(",",
        new string?[]
        {
            entry.OccurredUtc.ToString("O"), entry.EventId.ToString(),
            entry.Severity.ToString(), entry.Kind.ToString(), entry.ProfileId?.ToString(),
            entry.ProfileName, entry.EpisodeId?.ToString(), entry.OperationId?.ToString(),
            entry.PreviousState?.ToString(), entry.NewState?.ToString(),
            entry.Origin?.ToString(), entry.AttemptNumber?.ToString(),
            entry.AttemptLimit?.ToString(), entry.ProcessIdentity,
            entry.NativeErrorCode?.ToString(), entry.FailureCategory?.ToString()
        }.Select(value => "\"" + (value ?? "").Replace("\"", "\"\"") + "\""));

    private static string TextBlock(OperationalEvent entry) =>
        $"{entry.OccurredUtc:O} | {entry.Severity} | {entry.Kind} | {entry.ProfileName ?? "Relight"}\n" +
        $"  Event: {entry.EventId}  Profile: {entry.ProfileId?.ToString() ?? "—"}  Episode: {entry.EpisodeId?.ToString() ?? "—"}  Operation: {entry.OperationId?.ToString() ?? "—"}\n" +
        $"  State: {entry.PreviousState?.ToString() ?? "—"} → {entry.NewState?.ToString() ?? "—"}  Origin: {entry.Origin?.ToString() ?? "—"}  Attempt: {entry.AttemptNumber?.ToString() ?? "—"}/{entry.AttemptLimit?.ToString() ?? "—"}\n" +
        $"  Process identity: {entry.ProcessIdentity ?? "—"}  Native error: {entry.NativeErrorCode?.ToString() ?? "—"}  Failure category: {entry.FailureCategory?.ToString() ?? "—"}\n";
}
