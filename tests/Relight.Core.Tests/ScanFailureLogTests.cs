using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ScanFailureLogTests
{
    private static DiagnosticBuild Build => new("test", "Release", ".NET", "Windows", "X64", 4, 1);
    private static async Task<string> Export(string root, bool failures)
    {
        using var bytes = new MemoryStream();
        var reader = new ApplicationDiagnosticReader(root);
        if (failures) await reader.ExportFailuresAsync(bytes); else await reader.ExportAsync(bytes);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static ApplicationDiagnosticLine[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<ApplicationDiagnosticLine>(line, DiagnosticFileJournal.Json)!).ToArray();
    private static void Fail(ScanDiagnosticSession scan, string path, int code, ScanIoOperation operation = ScanIoOperation.ReadMetadata)
    {
        var error = new Win32Exception(code, @"private-message C:\private-path\secret");
        scan.Issue(StorageScanIssue.Failure(path, operation, error), error);
    }

    [Fact]
    public async Task SeparateFailureStreamRetainsLateCategoriesCountsCodesAndRecentSamples()
    {
        using var fixture = new Fixture(); var clock = new Clock();
        await using var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, Build, startWorker: false, clock: clock);
        var scan = diagnostics.BeginScan(@"C:\private-root", 100000)!;
        scan.Stage(DiagnosticScanStage.Traversing);
        for (int i = 0; i < 1000; i++) scan.Issue(new(@"C:\private-root\excluded-" + i, "expected exclusion")
            { Category = StorageScanIssueCategory.ReparseOrOffline, Operation = ScanIoOperation.ReadMetadata });
        diagnostics.Log.Drain();
        Assert.Empty(await Export(fixture.Path, true));
        Assert.Empty(Directory.GetFiles(diagnostics.DirectoryPath, "scan-failures-*.jsonl"));
        for (int i = 0; i < 200; i++) Fail(scan, @"C:\private-root\denied-" + i + ".log", 5);
        diagnostics.Sample(); diagnostics.Log.Drain(); clock.Advance(10);
        Fail(scan, @"C:\private-root\late-missing.log", 3);
        Fail(scan, @"C:\private-root\late-locked.log", 32);
        Fail(scan, @"C:\private-root\late-enumeration", 87, ScanIoOperation.EnumerateDirectory);
        Fail(scan, @"C:\private-root\newest-denied.log", 5);
        scan.End(DiagnosticScanStage.Canceled); diagnostics.Log.Drain();
        string failures = await Export(fixture.Path, true), general = await Export(fixture.Path, false);
        var records = Lines(failures);
        Assert.All(records, r => Assert.Contains(r.Entry.Kind, new[] { DiagnosticKind.ScanReadIssue, DiagnosticKind.ScanFailureSummary }));
        var final = records.Last(r => r.Entry.ScanFailures is not null);
        Assert.Equal(scan.Id, final.Entry.Scan!.ScanId);
        Assert.Equal(1000, final.Entry.Scan.Excluded);
        Assert.Equal(204, final.Entry.Scan.Failed);
        Assert.Equal(204, final.Entry.ScanFailures!.FailedEntries);
        Assert.Equal(204, final.Entry.ScanFailures.Groups.Sum(g => g.Count));
        var denied = Assert.Single(final.Entry.ScanFailures.Groups, g => g.NativeCode == 5);
        Assert.Equal(201, denied.Count);
        Assert.Equal(ApplicationDiagnostics.Tag(@"C:\private-root\denied-0.log"), denied.First.PathTag);
        Assert.Equal(ApplicationDiagnostics.Tag(@"C:\private-root\newest-denied.log"), denied.Latest.PathTag);
        Assert.Equal(clock.GetUtcNow(), denied.Latest.Utc);
        Assert.Contains(final.Entry.ScanFailures.Groups, g => g.NativeCode == 3);
        Assert.Contains(final.Entry.ScanFailures.Groups, g => g.NativeCode == 32);
        Assert.Contains(final.Entry.ScanFailures.Groups, g => g.NativeCode == 87 && g.Operation == ScanIoOperation.EnumerateDirectory);
        Assert.DoesNotContain("ScanReadIssue", general); Assert.DoesNotContain("ScanFailureSummary", general);
        Assert.Contains("ScanHeartbeat", general); Assert.Contains("ScanCanceled", general);
        foreach (string secret in new[] { "private-root", "private-message", "private-path", "expected exclusion" })
            Assert.DoesNotContain(secret, failures + general);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessNativeCodesAreBoundedWithoutLosingCountsAndShutdownFlushesPendingFailures(bool requestStop)
    {
        using var fixture = new Fixture();
        var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, Build, startWorker: false);
        var scan = diagnostics.BeginScan(@"C:\scan", 100000)!;
        for (int i = 0; i < 500; i++) Fail(scan, @"C:\scan\file-" + i, 1000 + i);
        Fail(scan, @"C:\scan\late-denied", 5);
        if (requestStop) diagnostics.RequestStop();
        await diagnostics.DisposeAsync();
        var records = Lines(await Export(fixture.Path, true));
        var summary = records.Last(r => r.Entry.ScanFailures is not null).Entry.ScanFailures!;
        Assert.Equal(501, summary.FailedEntries); Assert.True(summary.GroupingLimited);
        Assert.Equal(501, summary.Groups.Sum(g => g.Count));
        Assert.InRange(summary.Groups.Length, 1, ScanDiagnosticSession.DetailedFailureGroupLimit + 7);
        Assert.Contains(summary.Groups, g => g.OtherCodes && g.Category == StorageScanIssueCategory.AccessDenied && g.Count == 1);
        Assert.Contains(summary.Groups, g => g.OtherCodes && g.Category == StorageScanIssueCategory.NativeIoFailure && g.Count == 476);
        Assert.All(Directory.GetFiles(diagnostics.DirectoryPath, "scan-failures-*.jsonl"), file =>
            Assert.All(File.ReadLines(file), line => Assert.InRange(Encoding.UTF8.GetByteCount(line + "\n"), 1, 32768)));
    }

    [Fact]
    public async Task PersistenceFailureIsLoggedWithoutInventingFileFailuresAndBundleKeepsStreamsSeparate()
    {
        using var fixture = new Fixture();
        await using (var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, Build, startWorker: false))
        {
            var scan = diagnostics.BeginScan(@"C:\private-root", 100000)!;
            scan.Operation(ScanIoOperation.ReadMetadata, @"C:\private-root\last-file.log", 1, 10, 9, 0, 0, 9);
            scan.Stage(DiagnosticScanStage.Saving);
            scan.PersistenceFailure(new IOException(@"secret-message C:\private-path"));
            Assert.Equal(0, scan.Snapshot().Failed);
            scan.End(DiagnosticScanStage.Completed); diagnostics.Log.Drain();
        }
        string zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-failure-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await new DiagnosticBundleExporter(fixture.Path).ExportAsync(zip, null, null, "test");
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.GetEntry("scan-failures.jsonl")!.Open());
            string text = await reader.ReadToEndAsync();
            var failure = Assert.Single(Lines(text));
            Assert.Equal(DiagnosticKind.ScanFailed, failure.Entry.Kind);
            Assert.Equal(DiagnosticScanStage.Saving, failure.Entry.Scan!.Stage);
            Assert.Null(failure.Entry.Scan.Operation); Assert.Null(failure.Entry.Scan.PathTag);
            Assert.DoesNotContain("private-root", text); Assert.DoesNotContain("secret-message", text);
            Assert.NotNull(archive.GetEntry("application-diagnostics.jsonl"));
        }
        finally { if (File.Exists(zip)) File.Delete(zip); }
    }

    [Fact]
    public async Task FailureWriteErrorsRemainVisibleWhileGeneralDiagnosticsContinue()
    {
        using var fixture = new Fixture(); bool blocked = true; int written = 0;
        await using var log = new ApplicationDiagnosticLog(fixture.Path, appendFailures: _ =>
            { if (blocked) throw new IOException("test failure sink"); written++; });
        log.TryRecord(new(DiagnosticKind.ScanFailureSummary));
        log.TryRecord(new(DiagnosticKind.Heartbeat)); log.Drain();
        Assert.True(log.Degraded); Assert.Equal(1, log.LostRecords);
        Assert.Contains("Heartbeat", await Export(fixture.Path, false));
        blocked = false; log.TryRecord(new(DiagnosticKind.ScanFailureSummary)); log.Drain();
        Assert.Equal(1, written); Assert.True(log.Degraded); Assert.Equal(1, log.LostRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureExportIsBoundedAndRejectsMalformedAndForeignRecords(bool failuresOnly)
    {
        using var fixture = new Fixture(); Guid session = Guid.NewGuid();
        string name = failuresOnly ? "scan-failures" : "application";
        using (var journal = new DiagnosticFileJournal(fixture.Path, session,
            kind: failuresOnly ? DiagnosticJournalKind.ScanFailures : DiagnosticJournalKind.Application))
            for (int i = 0; i < 600; i++) journal.Append(new(1, session, 1, i, DateTimeOffset.UtcNow, 0,
                new(failuresOnly ? DiagnosticKind.ScanReadIssue : DiagnosticKind.Heartbeat)));
        string folder = System.IO.Path.Combine(fixture.Path, "Logs", "Diagnostics");
        File.AppendAllText(Directory.GetFiles(folder, name + "-*.jsonl").Single(), "{broken");
        File.WriteAllText(System.IO.Path.Combine(folder, name + "-notes.jsonl"), "private-message");
        string text = await Export(fixture.Path, failuresOnly);
        Assert.Equal(500, Lines(text).Length); Assert.DoesNotContain("private-message", text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 2 * 1024 * 1024);
        if (failuresOnly)
        {
            using var journal = new DiagnosticFileJournal(fixture.Path, Guid.NewGuid(), kind: DiagnosticJournalKind.ScanFailures);
            Assert.Throws<InvalidDataException>(() => journal.Append(new(1, session, 1, 1, DateTimeOffset.UtcNow, 0, new(DiagnosticKind.Heartbeat))));
        }
    }

    [Theory]
    [InlineData(DiagnosticJournalKind.Application)]
    [InlineData(DiagnosticJournalKind.ScanFailures)]
    public void RotationOnlyDeletesTheSelectedOwnedStream(DiagnosticJournalKind kind)
    {
        using var fixture = new Fixture(); Guid session = Guid.NewGuid();
        string folder = System.IO.Path.Combine(fixture.Path, "Logs", "Diagnostics"); Directory.CreateDirectory(folder);
        string otherName = kind == DiagnosticJournalKind.Application ? "scan-failures" : "application";
        string ownName = kind == DiagnosticJournalKind.Application ? "application" : "scan-failures";
        string other = System.IO.Path.Combine(folder, $"{otherName}-{Guid.NewGuid():N}-000000.jsonl");
        string foreign = System.IO.Path.Combine(folder, ownName + "-notes.jsonl");
        File.WriteAllText(other, "preserve other stream"); File.WriteAllText(foreign, "preserve notes");
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-8));
        var entry = new ApplicationDiagnosticEntry(kind == DiagnosticJournalKind.Application ? DiagnosticKind.Heartbeat : DiagnosticKind.ScanReadIssue);
        using (var journal = new DiagnosticFileJournal(fixture.Path, session, fileLimit: 1024, totalLimit: 8192, kind: kind))
            for (int i = 0; i < 100; i++) journal.Append(new(1, session, 1, i, DateTimeOffset.UtcNow, 0, entry));
        Assert.Equal("preserve other stream", File.ReadAllText(other)); Assert.Equal("preserve notes", File.ReadAllText(foreign));
        var owned = Directory.GetFiles(folder, $"{ownName}-{session:N}-*.jsonl");
        Assert.InRange(owned.Length, 1, 4); Assert.All(owned, file => Assert.True(new FileInfo(file).Length <= 1024));
    }

    private sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;
        public void Advance(int seconds) => _elapsed += TimeSpan.FromSeconds(seconds);
    }
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-failure-log-test-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
