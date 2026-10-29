using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ApplicationDiagnosticsTests
{
    private static DiagnosticBuild Build => new("test", "Release", ".NET", "Windows", "X64", 4, 1);
    private static DiagnosticResources Resources() => new(1, 2, 3, 4, 5, 6, 7, 8, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
    private sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;
        public void Advance(int seconds) => _elapsed += TimeSpan.FromSeconds(seconds);
    }
    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-diagnostics-" + Guid.NewGuid().ToString("N"));
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private static async Task<(string Text, ApplicationDiagnosticExport Result)> Export(string directory)
    {
        using var bytes = new MemoryStream();
        var result = await new ApplicationDiagnosticReader(directory).ExportAsync(bytes);
        return (Encoding.UTF8.GetString(bytes.ToArray()), result);
    }

    [Fact]
    public async Task DispatcherDelayUsesOnePendingProbeAndRecordsRecoveryAndWorkerIo()
    {
        using var fixture = new DirectoryFixture(); var clock = new Clock(); Action? reply = null; int probes = 0;
        await using var diagnostics = new ApplicationDiagnostics(fixture.Path, action => { reply = action; probes++; }, Build,
            startWorker: false, clock: clock, resources: Resources);
        var scan = diagnostics.BeginScan(@"C:\secret-root", 100000)!; scan.Stage(DiagnosticScanStage.Traversing);
        scan.Operation(ScanIoOperation.ReadMetadata, @"C:\secret-root\password-secret.vhdx", 4, 5000, 4000, 2, 3, 3997);
        diagnostics.Sample(); clock.Advance(21); diagnostics.Sample(); diagnostics.Log.Drain();
        Assert.Equal(1, probes);
        var stuck = scan.Snapshot(); Assert.True(stuck.IoPending); Assert.Equal(21, stuck.OperationSeconds);
        Assert.Equal(3997, stuck.Identities); Assert.Equal(5000, stuck.Visited);
        // A delayed sampler with an already acknowledged UI probe is not a delayed dispatcher.
        reply!(); clock.Advance(100); diagnostics.Sample(); diagnostics.Log.Drain();
        var (text, result) = await Export(fixture.Path);
        Assert.True(result.Records > 0); Assert.Contains("DispatcherDelayed", text); Assert.Contains("DispatcherRecovered", text);
        Assert.Contains("ReadMetadata", text); Assert.Contains("PendingPoolItems", text);
        Assert.DoesNotContain("secret-root", text); Assert.DoesNotContain("password-secret", text);
        Assert.Equal(".vhdx", stuck.Suffix); Assert.Equal(16, stuck.PathTag!.Length);
        scan.OperationEnded(); Assert.False(scan.Snapshot().IoPending);
        scan.End(DiagnosticScanStage.Canceled);
    }

    [Fact]
    public async Task BoundedQueueReportsLossAndWriteFailureRecoversWithoutProducerIo()
    {
        using var fixture = new DirectoryFixture(); string blocked = Path.Combine(fixture.Path, "Logs");
        File.WriteAllText(blocked, "owned test obstruction");
        await using var log = new ApplicationDiagnosticLog(fixture.Path, capacity: 2);
        Assert.True(log.TryRecord(new(DiagnosticKind.Heartbeat))); Assert.True(log.TryRecord(new(DiagnosticKind.Heartbeat)));
        Assert.False(log.TryRecord(new(DiagnosticKind.Heartbeat))); Assert.Equal(1, log.LostRecords);
        Assert.Equal("owned test obstruction", File.ReadAllText(blocked));
        log.Drain(); Assert.True(log.Degraded); Assert.NotNull(log.Problem); Assert.Equal(3, log.LostRecords);
        File.Delete(blocked); Directory.CreateDirectory(blocked);
        log.TryRecord(new(DiagnosticKind.Heartbeat)); log.Drain(); Assert.Null(log.Problem); Assert.NotNull(log.LastWriteUtc);
        Assert.True(log.Degraded); Assert.Contains("\"LostRecords\":3", (await Export(fixture.Path)).Text);
    }

    [Fact]
    public void RotationAndAgeRetentionOnlyRemoveOwnedDiagnosticFiles()
    {
        using var fixture = new DirectoryFixture(); string folder = Path.Combine(fixture.Path, "Logs", "Diagnostics");
        Directory.CreateDirectory(folder); string foreign = Path.Combine(folder, "application-user-notes.jsonl");
        File.WriteAllText(foreign, "preserve foreign file");
        string old = Path.Combine(folder, $"application-{Guid.NewGuid():N}-000000.jsonl"); File.WriteAllText(old, "old owned log");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8)); Guid session = Guid.NewGuid();
        using (var journal = new DiagnosticFileJournal(fixture.Path, session, fileLimit: 1024, totalLimit: 8192))
            for (int i = 0; i < 100; i++) journal.Append(new(1, session, 1, i, DateTimeOffset.UtcNow, 0, new(DiagnosticKind.Heartbeat)));
        Assert.Equal("preserve foreign file", File.ReadAllText(foreign)); Assert.False(File.Exists(old));
        var owned = Directory.GetFiles(folder, $"application-{session:N}-*.jsonl");
        Assert.InRange(owned.Length, 1, 4); Assert.All(owned, file => Assert.True(new FileInfo(file).Length <= 1024));
    }

    [Fact]
    public async Task ExceptionMessagesAndSourcePathsStayOutOfLogsAndDiagnosticBundle()
    {
        using var fixture = new DirectoryFixture(); using var exports = new DirectoryFixture();
        await using (var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, Build, startWorker: false))
        {
            try { throw new InvalidOperationException(@"password-secret C:\private-source\user-file.txt"); }
            catch (Exception error) { diagnostics.RecordException(new AggregateException(error)); }
            diagnostics.Log.Drain();
        }
        string zip = Path.Combine(exports.Path, "diagnostics.zip");
        await new DiagnosticBundleExporter(fixture.Path).ExportAsync(zip, null, null, "test");
        using var archive = ZipFile.OpenRead(zip);
        using var reader = new StreamReader(archive.GetEntry("application-diagnostics.jsonl")!.Open());
        string text = await reader.ReadToEndAsync(); Assert.Contains("InvalidOperationException", text); Assert.Contains("@IL", text);
        Assert.DoesNotContain("password-secret", text); Assert.DoesNotContain("private-source", text);
        Assert.Contains("ProcessStopped", text);
    }

    [Fact]
    public async Task ExportIsBoundedAndSkipsMalformedTailAndUnownedFiles()
    {
        using var fixture = new DirectoryFixture(); Guid session = Guid.NewGuid();
        string folder = Path.Combine(fixture.Path, "Logs", "Diagnostics");
        using (var journal = new DiagnosticFileJournal(fixture.Path, session))
            for (int i = 0; i < 600; i++) journal.Append(new(1, session, 1, i, DateTimeOffset.UtcNow, 0, new(DiagnosticKind.Heartbeat)));
        File.AppendAllText(Directory.GetFiles(folder, "application-*.jsonl").Single(), "{broken");
        File.WriteAllText(Path.Combine(folder, "notes.jsonl"), "password-secret");
        var (text, result) = await Export(fixture.Path); Assert.Equal(500, result.Records); Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain("password-secret", text); Assert.True(Encoding.UTF8.GetByteCount(text) <= 2 * 1024 * 1024);
    }

    [Fact]
    public async Task ScanIssueSamplingIsBoundedAndFinishedCountsAreCorrelated()
    {
        using var fixture = new DirectoryFixture(); await using var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, Build, startWorker: false);
        var scan = diagnostics.BeginScan(@"C:\scan", 100000)!;
        for (int i = 0; i < 100; i++) scan.ReadFailure(new UnauthorizedAccessException("secret-message"));
        var snapshot = new StorageScanSnapshot(1, @"C:\scan", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false,
            100000, 90000, 100, 0, true, [], []);
        scan.TraversalCompleted(snapshot); scan.Stage(DiagnosticScanStage.Saving); scan.End(DiagnosticScanStage.Completed, snapshot);
        diagnostics.Log.Drain(); var (text, _) = await Export(fixture.Path);
        var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(5, records.Count(r => r.RootElement.GetProperty("Entry").GetProperty("Kind").GetString() == "ScanReadIssue"));
            var finished = records.Single(r => r.RootElement.GetProperty("Entry").GetProperty("Kind").GetString() == "ScanFinished");
            Assert.Equal(scan.Id, finished.RootElement.GetProperty("Entry").GetProperty("Scan").GetProperty("ScanId").GetGuid());
            Assert.Equal(90000, finished.RootElement.GetProperty("Entry").GetProperty("Scan").GetProperty("Measured").GetInt32());
            Assert.DoesNotContain("secret-message", text);
        }
        finally { foreach (var record in records) record.Dispose(); }
    }
}
