using System.Text.Json;
using System.Text.Json.Serialization;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class OperationalEventJournalTests
{
    [Fact]
    public async Task Append_writes_structured_UTC_events_without_unstructured_process_data()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path, GlobalConfiguration.Default);
        Guid profile = Guid.NewGuid();
        Guid operation = Guid.NewGuid();
        OperationalEvent entry = NewEvent() with
        {
            ProfileId = profile,
            OperationId = operation,
            Kind = OperationalEventKind.LaunchReserved,
            AttemptNumber = 1,
            AttemptLimit = 3
        };

        EventJournalStatus result = await journal.AppendAsync(entry);
        Assert.False(result.Degraded);
        string path = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Logs")));
        using JsonDocument parsed = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        JsonElement root = parsed.RootElement;
        Assert.Equal(entry.EventId.ToString(), root.GetProperty("eventId").GetString());
        Assert.Equal("LaunchReserved", root.GetProperty("kind").GetString());
        Assert.Equal(profile.ToString(), root.GetProperty("profileId").GetString());
        Assert.Equal(operation.ToString(), root.GetProperty("operationId").GetString());
        Assert.False(root.TryGetProperty("arguments", out _));
        Assert.False(root.TryGetProperty("environment", out _));
    }

    [Fact]
    public async Task Failure_buffers_with_a_bound_and_replays_when_log_directory_recovers()
    {
        using var directory = new TestDirectory();
        string logs = Path.Combine(directory.Path, "Logs");
        File.WriteAllText(logs, "Controlled obstruction");
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default, bufferLimit: 2);
        for (int i = 0; i < 3; i++)
        {
            EventJournalStatus failed = await journal.AppendAsync(NewEvent());
            Assert.True(failed.Degraded);
        }
        EventJournalStatus buffered = await journal.GetStatusAsync();
        Assert.Equal(2, buffered.BufferedCount);
        Assert.Equal(1, buffered.DroppedCount);

        File.Delete(logs);
        EventJournalStatus recovered = await journal.AppendAsync(NewEvent());
        Assert.False(recovered.Degraded);
        Assert.Equal(0, recovered.BufferedCount);
        Assert.Equal(1, recovered.DroppedCount);
        string path = Assert.Single(Directory.GetFiles(logs));
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task Retention_deletes_only_old_owned_logs()
    {
        using var directory = new TestDirectory();
        string logs = Path.Combine(directory.Path, "Logs");
        Directory.CreateDirectory(logs);
        string owned = Path.Combine(logs,
            $"events-20200101T0000000000000Z-{Guid.NewGuid():N}.jsonl");
        string unowned = Path.Combine(logs, "events-my-notes.jsonl");
        string configuration = Path.Combine(directory.Path, "configuration.json");
        File.WriteAllText(owned, "Old journal");
        File.SetLastWriteTimeUtc(owned, DateTime.UtcNow.AddDays(-40));
        File.WriteAllText(unowned, "Do not delete");
        File.SetLastWriteTimeUtc(unowned, DateTime.UtcNow.AddDays(-40));
        File.WriteAllText(configuration, "User configuration");

        using var journal = new OperationalEventJournal(directory.Path, GlobalConfiguration.Default);
        Assert.False((await journal.AppendAsync(NewEvent())).Degraded);
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(unowned));
        Assert.True(File.Exists(configuration));
    }

    [Fact]
    public async Task Rotation_and_total_size_retention_keep_owned_logs_bounded()
    {
        using var directory = new TestDirectory();
        var settings = GlobalConfiguration.Default with
        {
            MaximumLogBytes = 1_048_576,
            LogRotationBytes = 65_536
        };
        using var journal = new OperationalEventJournal(directory.Path, settings);
        for (int i = 0; i < 30; i++)
        {
            EventJournalStatus status = await journal.AppendAsync(NewEvent() with
            {
                ProcessIdentity = new string('x', 40_000)
            });
            Assert.False(status.Degraded);
        }
        string[] files = Directory.GetFiles(Path.Combine(directory.Path, "Logs"), "*.jsonl");
        Assert.True(files.Length > 1);
        Assert.True(files.Length < 30);
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= settings.MaximumLogBytes);
    }

    [Fact]
    public async Task History_reader_filters_and_returns_latest_events_with_truncation()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid firstProfile = Guid.NewGuid();
        Guid secondProfile = Guid.NewGuid();
        DateTimeOffset origin = DateTimeOffset.UtcNow.AddHours(-1);
        for (int i = 0; i < 5; i++)
        {
            await journal.AppendAsync(NewEvent() with
            {
                OccurredUtc = origin.AddMinutes(i),
                ProfileId = i == 4 ? secondProfile : firstProfile,
                Kind = i % 2 == 0 ? OperationalEventKind.LaunchReserved :
                    OperationalEventKind.StateChanged,
                Severity = i == 3 ? EventSeverity.Warning : EventSeverity.Information
            });
        }
        var reader = new OperationalEventHistoryReader(directory.Path);
        EventHistoryResult result = await reader.ReadAsync(new(
            ProfileId: firstProfile, Limit: 2));
        Assert.Equal(4, result.TotalMatches);
        Assert.True(result.Truncated);
        Assert.Equal(origin.AddMinutes(3), result.Events[0].OccurredUtc);
        Assert.Equal(origin.AddMinutes(2), result.Events[1].OccurredUtc);
        Assert.Equal(0, result.SkippedMalformedLines);

        EventHistoryResult filtered = await reader.ReadAsync(new(
            ProfileId: firstProfile, MinimumSeverity: EventSeverity.Warning));
        Assert.Single(filtered.Events);
        Assert.Equal(OperationalEventKind.StateChanged, filtered.Events[0].Kind);
    }

    [Fact]
    public async Task History_reader_ignores_foreign_files_and_reports_corrupt_lines()
    {
        using var directory = new TestDirectory();
        string logs = Path.Combine(directory.Path, "Logs");
        Directory.CreateDirectory(logs);
        string owned = Path.Combine(logs,
            $"events-20260927T0000000000000Z-{Guid.NewGuid():N}.jsonl");
        string foreign = Path.Combine(logs, "events-my-private-notes.jsonl");
        File.WriteAllText(owned, "{bad json}\n");
        File.WriteAllText(foreign, JsonSerializer.Serialize(NewEvent()));

        EventHistoryResult result = await new OperationalEventHistoryReader(directory.Path)
            .ReadAsync(new());
        Assert.Empty(result.Events);
        Assert.Equal(1, result.SkippedMalformedLines);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Filtered_export_includes_full_episode_beyond_display_limit()
    {
        using var directory = new TestDirectory();
        using var exports = new TestDirectory();
        string logs = Path.Combine(directory.Path, "Logs");
        Directory.CreateDirectory(logs);
        string owned = Path.Combine(logs,
            $"events-20260927T0000000000000Z-{Guid.NewGuid():N}.jsonl");
        Guid episode = Guid.NewGuid();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        OperationalEvent[] entries = Enumerable.Range(0, 506)
            .Select(i => NewEvent() with
            {
                OccurredUtc = DateTimeOffset.UtcNow.AddMinutes(i),
                EpisodeId = i == 505 ? Guid.NewGuid() : episode,
                ProfileName = "Alpha,\"Beta\"",
                Kind = OperationalEventKind.LaunchReserved
            }).ToArray();
        File.WriteAllLines(owned, entries.Select(entry => JsonSerializer.Serialize(entry, json)));
        var reader = new OperationalEventHistoryReader(directory.Path);
        EventHistoryQuery query = new(EpisodeId: episode);
        EventHistoryResult display = await reader.ReadAsync(query);
        Assert.Equal(500, display.Events.Count);
        Assert.Equal(505, display.TotalMatches);

        string csv = Path.Combine(exports.Path, "episode.csv");
        EventHistoryExportResult exported = await reader.ExportAsync(query, csv,
            EventHistoryExportFormat.Csv);
        Assert.Equal(505, exported.ExportedEvents);
        Assert.Equal(0, exported.SkippedMalformedLines);
        string[] lines = File.ReadAllLines(csv);
        Assert.Equal(506, lines.Length);
        Assert.Contains("\"Alpha,\"\"Beta\"\"\"", lines[1]);
        Assert.DoesNotContain(entries[505].EpisodeId!.Value.ToString(),
            File.ReadAllText(csv));

        string text = Path.Combine(exports.Path, "episode.txt");
        EventHistoryExportResult textResult = await reader.ExportAsync(query, text,
            EventHistoryExportFormat.Text);
        Assert.Equal(505, textResult.ExportedEvents);
        Assert.Contains("LaunchReserved", File.ReadAllText(text));
    }

    [Fact]
    public async Task Export_refuses_to_overwrite_relight_state()
    {
        using var directory = new TestDirectory();
        string configuration = Path.Combine(directory.Path, "configuration.json");
        File.WriteAllText(configuration, "untouched");
        var reader = new OperationalEventHistoryReader(directory.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ExportAsync(new(),
            configuration, EventHistoryExportFormat.Csv));
        Assert.Equal("untouched", File.ReadAllText(configuration));
    }

    [Fact]
    public async Task Cancelled_export_preserves_existing_destination()
    {
        using var directory = new TestDirectory();
        using var exports = new TestDirectory();
        string destination = Path.Combine(exports.Path, "history.csv");
        File.WriteAllText(destination, "previous export");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OperationalEventHistoryReader(directory.Path).ExportAsync(new(),
                destination, EventHistoryExportFormat.Csv, cancellation.Token));
        Assert.Equal("previous export", File.ReadAllText(destination));
        Assert.Single(Directory.GetFiles(exports.Path));
    }

    private static OperationalEvent NewEvent() =>
        new(DateTimeOffset.UtcNow, Guid.NewGuid(), EventSeverity.Information,
            OperationalEventKind.Startup);

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-journal-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
