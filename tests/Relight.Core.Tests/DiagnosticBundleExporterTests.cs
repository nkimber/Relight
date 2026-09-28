using System.IO.Compression;
using Relight.Core;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class DiagnosticBundleExporterTests
{
    [Fact]
    public async Task Bundle_redacts_launch_data_and_event_process_paths()
    {
        using var data = new TestDirectory();
        using var exports = new TestDirectory();
        Guid profileId = Guid.NewGuid();
        var profile = new ProfileConfiguration(profileId, "Disposable app", true,
            new(TargetKind.Executable, @"C:\folder-secret\app.exe",
                ["argument-secret"], @"C:\working-secret",
                "required-secret", "excluded-secret"), RecoveryPolicy.Default);
        StoredConfiguration configuration = new ConfigurationStore(data.Path)
            .Initialize(new([profile], GlobalConfiguration.Default));
        using (var journal = new OperationalEventJournal(data.Path,
                   GlobalConfiguration.Default))
            await journal.AppendAsync(new(DateTimeOffset.UtcNow, Guid.NewGuid(),
                EventSeverity.Warning, OperationalEventKind.DetectionUnavailable,
                ProfileId: profileId, ProcessIdentity: "path-secret",
                NativeErrorCode: 5,
                FailureCategory: OperationalFailureCategory.PermissionDenied));
        string destination = Path.Combine(exports.Path, "diagnostics.zip");

        DiagnosticBundleResult result = await new DiagnosticBundleExporter(data.Path)
            .ExportAsync(destination, configuration, null, "1.2.3",
                targets: [new DiagnosticTargetStatus(profileId, TargetKind.Executable,
                    Monitoring: true, AutomaticActionsAllowed: false,
                    Detection: DetectionKind.Unavailable,
                    RecoveryState: RecoveryState.AwaitingIntervention,
                    DetectionUnavailable: true, LockedOut: true,
                    HasProblem: true)]);

        Assert.Equal(1, result.ExportedEvents);
        Assert.Equal(0, result.SkippedMalformedLines);
        using ZipArchive bundle = ZipFile.OpenRead(destination);
        string metadata = await Read(bundle, "diagnostics.json");
        string events = await Read(bundle, "events.jsonl");
        Assert.Contains("1.2.3", metadata);
        Assert.Contains("Executable", metadata);
        Assert.Contains("Unavailable", metadata);
        Assert.Contains("AwaitingIntervention", metadata);
        Assert.Contains("hasProblem", metadata);
        Assert.DoesNotContain("app.exe", metadata);
        Assert.Contains("DetectionUnavailable", events);
        Assert.Contains("PermissionDenied", events);
        Assert.Contains("nativeErrorCode\":5", events);
        foreach (string secret in new[] { "folder-secret", "working-secret",
                     "argument-secret", "required-secret", "excluded-secret", "path-secret" })
        {
            Assert.DoesNotContain(secret, metadata);
            Assert.DoesNotContain(secret, events);
        }
        Assert.Single(events.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Bundle_rejects_internal_destination_and_preserves_existing_on_cancellation()
    {
        using var data = new TestDirectory();
        using var exports = new TestDirectory();
        var exporter = new DiagnosticBundleExporter(data.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(
            Path.Combine(data.Path, "State", "bundle.zip"), null, null, "1.0"));
        string destination = Path.Combine(exports.Path, "diagnostics.zip");
        File.WriteAllText(destination, "previous export");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(destination, null, null, "1.0", cancellation.Token));

        Assert.Equal("previous export", File.ReadAllText(destination));
        Assert.Single(Directory.GetFiles(exports.Path));
    }

    [Fact]
    public async Task Bundle_includes_early_and_late_events_beyond_dashboard_row_limit()
    {
        using var data = new TestDirectory();
        using var exports = new TestDirectory();
        Guid episode = Guid.NewGuid();
        Guid firstEvent = Guid.NewGuid();
        Guid lastEvent = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-1);
        using (var journal = new OperationalEventJournal(data.Path,
                   GlobalConfiguration.Default))
        {
            for (int index = 0; index < 502; index++)
                await journal.AppendAsync(new(start.AddSeconds(index),
                    index == 0 ? firstEvent : index == 501 ? lastEvent : Guid.NewGuid(),
                    EventSeverity.Information,
                    index == 0 ? OperationalEventKind.LaunchReserved :
                    index == 501 ? OperationalEventKind.ObservationCompleted :
                    OperationalEventKind.StateChanged,
                    EpisodeId: index is 0 or 501 ? episode : null,
                    ProcessIdentity: "private-process-path"));
        }
        string destination = Path.Combine(exports.Path, "diagnostics.zip");

        DiagnosticBundleResult result = await new DiagnosticBundleExporter(data.Path)
            .ExportAsync(destination, null, null, "1.0");

        Assert.Equal(502, result.ExportedEvents);
        using ZipArchive bundle = ZipFile.OpenRead(destination);
        string metadata = await Read(bundle, "diagnostics.json");
        string events = await Read(bundle, "events.jsonl");
        Assert.Contains("\"exported\": 502", metadata);
        Assert.Contains("\"rowLimitApplied\": false", metadata);
        Assert.Contains(firstEvent.ToString(), events);
        Assert.Contains(lastEvent.ToString(), events);
        Assert.DoesNotContain("private-process-path", events);
        Assert.Equal(502, events.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static async Task<string> Read(ZipArchive archive, string name)
    {
        await using Stream stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-diagnostics-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
