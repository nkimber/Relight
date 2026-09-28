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
    public async Task Retention_waits_for_an_active_export_lease_without_degrading_logging()
    {
        using var directory = new TestDirectory();
        string logs = Path.Combine(directory.Path, "Logs");
        Directory.CreateDirectory(logs);
        string owned = Path.Combine(logs,
            $"events-20200101T0000000000000Z-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(owned, "Old journal");
        File.SetLastWriteTimeUtc(owned, DateTime.UtcNow.AddDays(-40));
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        string gate = Path.Combine(directory.Path, ".relight-log-retention.lock");
        using (var exportLease = new FileStream(gate, FileMode.OpenOrCreate,
                   FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            EventJournalStatus status = await journal.AppendAsync(NewEvent());
            Assert.False(status.Degraded);
            Assert.True(File.Exists(owned));
        }

        Assert.False((await journal.AppendAsync(NewEvent())).Degraded);
        Assert.False(File.Exists(owned));
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
    public async Task Overnight_summary_counts_full_period_independently_of_list_filters()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid profile = Guid.NewGuid();
        Guid another = Guid.NewGuid();
        OperationalEventKind[] kinds =
        [
            OperationalEventKind.TargetDisappeared,
            OperationalEventKind.LaunchReserved,
            OperationalEventKind.LaunchDispatched,
            OperationalEventKind.ObservationCompleted,
            OperationalEventKind.LockoutEntered,
            OperationalEventKind.MonitoringGap,
            OperationalEventKind.MonitoringRestored
        ];
        foreach (OperationalEventKind kind in kinds)
            await journal.AppendAsync(NewEvent() with
            {
                ProfileId = profile,
                Kind = kind,
                Origin = Relight.Core.ObservationOrigin.AutomaticLaunch,
                Severity = kind == OperationalEventKind.LaunchReserved
                    ? EventSeverity.Warning : EventSeverity.Information
            });
        await journal.AppendAsync(NewEvent() with
        {
            ProfileId = another,
            ProfileName = "Removed app",
            Kind = OperationalEventKind.ProfileRemoved
        });

        EventHistoryOverview overview = await new OperationalEventHistoryReader(directory.Path)
            .ReadOverviewAsync(new(ProfileId: profile,
                MinimumSeverity: EventSeverity.Warning,
                Kind: OperationalEventKind.LaunchReserved));
        Assert.Single(overview.Results.Events);
        Assert.Equal(1, overview.Summary.ObservedDisappearances);
        Assert.Equal(1, overview.Summary.AutomaticAttemptsReserved);
        Assert.Equal(1, overview.Summary.AutomaticLaunchesDispatched);
        Assert.Equal(1, overview.Summary.StableAutomaticRecoveries);
        Assert.Equal(0, overview.Summary.OtherStableStarts);
        Assert.Equal(1, overview.Summary.Lockouts);
        Assert.Equal(1, overview.Summary.MonitoringGaps);
        Assert.Equal(1, overview.Summary.MonitoringRestorations);
        Assert.Contains(overview.Profiles, item => item.Id == another &&
            item.Name == "Removed app");
        Assert.Contains(overview.Profiles, item => item.Id == profile);
    }

    [Fact]
    public async Task Overnight_summary_counts_only_complete_monitoring_gap_pairs_by_profile()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        DateTimeOffset origin = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        // Deliberately append out of time order, as rotated files can be scanned
        // in a different order than the events occurred.
        await journal.AppendAsync(NewEvent() with { ProfileId = first,
            Kind = OperationalEventKind.MonitoringRestored,
            OccurredUtc = origin.AddMinutes(12) });
        await journal.AppendAsync(NewEvent() with { ProfileId = second,
            Kind = OperationalEventKind.MonitoringRestored,
            OccurredUtc = origin.AddMinutes(8) });
        await journal.AppendAsync(NewEvent() with { ProfileId = first,
            Kind = OperationalEventKind.MonitoringGap,
            OccurredUtc = origin.AddMinutes(2) });
        await journal.AppendAsync(NewEvent() with { ProfileId = second,
            Kind = OperationalEventKind.MonitoringGap,
            OccurredUtc = origin.AddMinutes(5) });
        await journal.AppendAsync(NewEvent() with { ProfileId = first,
            Kind = OperationalEventKind.MonitoringGap,
            OccurredUtc = origin.AddMinutes(20) });

        var reader = new OperationalEventHistoryReader(directory.Path);
        EventHistoryOverview all = await reader.ReadOverviewAsync(new(
            MinimumSeverity: EventSeverity.Error));
        Assert.Equal(TimeSpan.FromMinutes(13),
            all.Summary.PairedMonitoringGapTimestampSpan);
        Assert.Equal(1, all.Summary.UnpairedMonitoringTransitions);

        EventHistoryOverview firstOnly = await reader.ReadOverviewAsync(new(
            ProfileId: first, FromUtc: origin.AddMinutes(3),
            ThroughUtc: origin.AddMinutes(21)));
        Assert.Equal(TimeSpan.FromMinutes(9),
            firstOnly.Summary.PairedMonitoringGapTimestampSpan);
        Assert.Equal(1, firstOnly.Summary.UnpairedMonitoringTransitions);
    }

    [Fact]
    public async Task Overnight_summary_clips_complete_gap_pairs_across_both_period_edges()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid profile = Guid.NewGuid();
        DateTimeOffset origin = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        await journal.AppendAsync(NewEvent() with { ProfileId = profile,
            Kind = OperationalEventKind.MonitoringRestored,
            OccurredUtc = origin.AddMinutes(20) });
        await journal.AppendAsync(NewEvent() with { ProfileId = profile,
            Kind = OperationalEventKind.MonitoringGap,
            OccurredUtc = origin.AddMinutes(-5) });
        await journal.AppendAsync(NewEvent() with { ProfileId = profile,
            Kind = OperationalEventKind.MonitoringRestored,
            OccurredUtc = origin.AddMinutes(-10) });

        EventHistoryOverview selected = await new OperationalEventHistoryReader(directory.Path)
            .ReadOverviewAsync(new(ProfileId: profile, FromUtc: origin,
                ThroughUtc: origin.AddMinutes(10)));

        Assert.Equal(TimeSpan.FromMinutes(10),
            selected.Summary.PairedMonitoringGapTimestampSpan);
        Assert.Equal(0, selected.Summary.MonitoringGaps);
        Assert.Equal(0, selected.Summary.MonitoringRestorations);
        Assert.Equal(0, selected.Summary.UnpairedMonitoringTransitions);
    }

    [Fact]
    public async Task Unclosed_gap_before_period_has_unknown_duration()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid profile = Guid.NewGuid();
        DateTimeOffset origin = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        await journal.AppendAsync(NewEvent() with { ProfileId = profile,
            Kind = OperationalEventKind.MonitoringGap,
            OccurredUtc = origin.AddMinutes(-5) });

        EventHistoryOverview selected = await new OperationalEventHistoryReader(directory.Path)
            .ReadOverviewAsync(new(ProfileId: profile, FromUtc: origin,
                ThroughUtc: origin.AddMinutes(10)));

        Assert.Equal(TimeSpan.Zero, selected.Summary.PairedMonitoringGapTimestampSpan);
        Assert.Equal(1, selected.Summary.UnpairedMonitoringTransitions);
    }

    [Fact]
    public async Task Dashboard_milestones_use_latest_recorded_outage_and_stable_automatic_recovery()
    {
        using var directory = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid profile = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await journal.AppendAsync(NewEvent() with
        {
            ProfileId = profile, Kind = OperationalEventKind.TargetDisappeared,
            OccurredUtc = now.AddMinutes(-10)
        });
        await journal.AppendAsync(NewEvent() with
        {
            ProfileId = profile, Kind = OperationalEventKind.TargetDisappeared,
            OccurredUtc = now.AddMinutes(-5)
        });
        await journal.AppendAsync(NewEvent() with
        {
            ProfileId = profile, Kind = OperationalEventKind.ObservationCompleted,
            Origin = Relight.Core.ObservationOrigin.ExternalStart,
            OccurredUtc = now.AddMinutes(-4)
        });
        await journal.AppendAsync(NewEvent() with
        {
            ProfileId = profile, Kind = OperationalEventKind.ObservationCompleted,
            Origin = Relight.Core.ObservationOrigin.AutomaticLaunch,
            OccurredUtc = now.AddMinutes(-3)
        });

        DashboardEventHistory result = await new OperationalEventHistoryReader(directory.Path)
            .ReadDashboardMilestonesAsync();

        DashboardEventMilestones milestones = result.Profiles[profile];
        Assert.Equal(now.AddMinutes(-5), milestones.LastOutageUtc);
        Assert.Equal(now.AddMinutes(-3), milestones.LastAutomaticRecoveryUtc);
        Assert.Equal(0, result.SkippedMalformedLines);
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
                Kind = OperationalEventKind.LaunchReserved,
                FailureCategory = OperationalFailureCategory.PermissionDenied,
                NativeErrorCode = 5
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
        Assert.EndsWith(",failureCategory", lines[0]);
        Assert.Contains("\"Alpha,\"\"Beta\"\"\"", lines[1]);
        Assert.EndsWith(",\"PermissionDenied\"", lines[1]);
        Assert.DoesNotContain(entries[505].EpisodeId!.Value.ToString(),
            File.ReadAllText(csv));

        string text = Path.Combine(exports.Path, "episode.txt");
        EventHistoryExportResult textResult = await reader.ExportAsync(query, text,
            EventHistoryExportFormat.Text);
        Assert.Equal(505, textResult.ExportedEvents);
        Assert.Contains("LaunchReserved", File.ReadAllText(text));
        Assert.Contains("Failure category: PermissionDenied", File.ReadAllText(text));
    }

    [Fact]
    public async Task Episode_export_ignores_row_filters_and_redacts_process_identity()
    {
        using var directory = new TestDirectory();
        using var exports = new TestDirectory();
        using var journal = new OperationalEventJournal(directory.Path,
            GlobalConfiguration.Default);
        Guid episode = Guid.NewGuid();
        Guid profile = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await journal.AppendAsync(NewEvent() with
        {
            EpisodeId = episode, ProfileId = profile,
            OccurredUtc = now.AddHours(-2),
            Kind = OperationalEventKind.TargetDisappeared,
            ProcessIdentity = @"C:\private-path\secret.exe|123"
        });
        await journal.AppendAsync(NewEvent() with
        {
            EpisodeId = episode, ProfileId = profile,
            OccurredUtc = now,
            Kind = OperationalEventKind.LaunchReserved,
            Severity = EventSeverity.Warning
        });
        await journal.AppendAsync(NewEvent() with
        {
            EpisodeId = Guid.NewGuid(), ProfileId = profile,
            Kind = OperationalEventKind.LaunchReserved
        });
        var reader = new OperationalEventHistoryReader(directory.Path);
        EventHistoryQuery query = new(ProfileId: Guid.NewGuid(),
            MinimumSeverity: EventSeverity.Warning,
            Kind: OperationalEventKind.LaunchReserved, EpisodeId: episode,
            FromUtc: now.AddMinutes(-1), ThroughUtc: now.AddMinutes(1));
        string csv = Path.Combine(exports.Path, "episode.csv");
        EventHistoryExportResult result = await reader.ExportAsync(query, csv,
            EventHistoryExportFormat.Csv);

        Assert.Equal(2, result.ExportedEvents);
        string content = File.ReadAllText(csv);
        Assert.Contains("TargetDisappeared", content);
        Assert.Contains("LaunchReserved", content);
        Assert.DoesNotContain("private-path", content);
        Assert.DoesNotContain("secret.exe", content);
        string textPath = Path.Combine(exports.Path, "episode.txt");
        EventHistoryExportResult textResult = await reader.ExportAsync(query, textPath,
            EventHistoryExportFormat.Text);
        Assert.Equal(2, textResult.ExportedEvents);
        Assert.DoesNotContain("private-path", File.ReadAllText(textPath));
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
