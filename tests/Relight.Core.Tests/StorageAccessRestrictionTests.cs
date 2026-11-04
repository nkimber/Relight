using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class StorageAccessRestrictionTests
{
    [Theory]
    [InlineData(@"C:\scan", @"C:\scan\folder\file", ScanIoOperation.ReadMetadata, @"C:\scan\folder")]
    [InlineData(@"C:\scan", @"C:\scan\folder", ScanIoOperation.EnumerateDirectory, @"C:\scan\folder")]
    [InlineData(@"C:\scan", @"C:\scan", ScanIoOperation.RootValidation, @"C:\scan")]
    [InlineData(@"C:\", @"C:\denied", ScanIoOperation.ReadMetadata, @"C:\")]
    [InlineData(@"C:\scan", @"C:\scan\one\two\three\four\five\six\file", ScanIoOperation.ReadMetadata, @"C:\scan\one\two\three\four\five")]
    [InlineData(@"C:\scan", @"C:\scan\one\unused\..\file", ScanIoOperation.ReadMetadata, @"C:\scan\one")]
    [InlineData(@"C:\scan", @"C:\scan-lookalike\file", ScanIoOperation.ReadMetadata, null)]
    [InlineData(@"C:\scan", @"C:\scan\..\outside\file", ScanIoOperation.ReadMetadata, null)]
    [InlineData(@"C:\scan", @"relative\file", ScanIoOperation.ReadMetadata, null)]
    [InlineData(@"C:\scan", @"\\server\share\file", ScanIoOperation.ReadMetadata, null)]
    public void FoldersUseOperationBoundariesAndBoundedDepth(string root, string path, ScanIoOperation operation, string? expected)
    {
        Assert.Equal(expected, StorageAccessRestrictionCollector.FolderFor(root, Denied(path, operation)));
    }

    [Fact]
    public void CountsSurviveSampleStormsCaseDifferencesAndDeepScratchFolders()
    {
        var collector = new StorageAccessRestrictionCollector(@"C:\");
        var samples = new StorageScanIssueCollector();
        for (int i = 0; i < 10000; i++) Add(new(@"C:\excluded\" + i, "excluded") { Category = StorageScanIssueCategory.ReparseOrOffline });
        for (int i = 0; i < 315; i++) Add(Denied(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\report-" + i));
        for (int i = 0; i < 76; i++) Add(Denied(@"C:\Users\person\AppData\Local\Temp\scratch-" + i + @"\lock"));
        for (int i = 0; i < 287; i++) Add(Denied(@"C:\Windows\System32\winevt\Logs\event-" + i));
        for (int i = 0; i < 257; i++) Add(Denied((i % 2 == 0 ? @"C:\other\" : @"c:\OTHER\") + i));
        Add(Denied(@"C:\protected\key") with { Category = StorageScanIssueCategory.ProtectedSystemAccess });
        Add(StorageScanIssue.Failure(@"C:\other\disk-error", ScanIoOperation.ReadMetadata, new Win32Exception(1117)));

        var summary = collector.Summary();
        Assert.Equal(935, summary.Folders.Sum(f => f.Count)); Assert.Equal(0, summary.UngroupedFailures);
        Assert.Equal(4, summary.Folders.Length);
        Assert.Contains(summary.Folders, f => f.Folder == @"C:\Users\person\AppData\Local\Temp" && f.Count == 76);
        Assert.Contains(summary.Folders, f => f.Folder == @"C:\other" && f.Count == 257);
        Assert.Equal(10, samples.Samples().Count(i => i.Category == StorageScanIssueCategory.AccessDenied));
        Assert.Equal(315, summary.Folders[0].Count);
        void Add(StorageScanIssue issue) { collector.Add(issue); samples.Add(issue); }
    }

    [Fact]
    public void FolderAndPathLimitsKeepExactRemainderAndPreviouslyAdmittedCounts()
    {
        var collector = new StorageAccessRestrictionCollector(@"C:\scan");
        for (int i = 0; i < StorageAccessRestrictionSummary.MaximumFolders; i++)
            collector.Add(Denied($@"C:\scan\folder-{i}\file"));
        for (int i = 0; i < 40; i++) collector.Add(Denied($@"C:\scan\overflow-{i}\file"));
        collector.Add(Denied(@"C:\SCAN\FOLDER-0\late"));
        string tooLong = @"C:\scan\" + string.Join('\\', Enumerable.Repeat(new string('x', 250), 5));
        collector.Add(Denied(tooLong, ScanIoOperation.EnumerateDirectory));
        collector.Add(Denied(@"C:\outside\file"));
        var summary = collector.Summary();
        Assert.Equal(StorageAccessRestrictionSummary.MaximumFolders, summary.Folders.Length);
        Assert.Equal(42, summary.UngroupedFailures);
        Assert.Equal(2, Assert.Single(summary.Folders, f => f.Folder == @"C:\scan\folder-0").Count);
        Assert.Equal(StorageAccessRestrictionSummary.MaximumFolders + 43, summary.Folders.Sum(f => f.Count) + summary.UngroupedFailures);
    }

    [Fact]
    public async Task ScanStoreUiAndRedactedDiagnosticsKeepRestrictionsSeparateFromOtherErrors()
    {
        using var fixture = new Fixture();
        await using var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, new("test", "Release", ".NET", "Windows", "X64", 1, 1), startWorker: false);
        var scan = diagnostics.BeginScan(@"C:\scan", 100)!;
        var result = new StorageScanner(new Files()).Scan(@"C:\scan", diagnostics: scan);
        scan.End(DiagnosticScanStage.Completed, result); diagnostics.Log.Drain();
        Assert.Equal(8, result.FailedEntries); Assert.Equal(6, result.AccessRestrictedEntries);
        Assert.Equal(0, result.ProtectedSystemFailedEntries); Assert.Equal(2, result.OtherErrorEntries);
        Assert.Equal(1, result.ExcludedEntries); Assert.False(result.CoverageComplete);
        Assert.Equal(6, result.AccessRestrictions!.Folders.Sum(f => f.Count));
        Assert.Equal(3, result.AccessRestrictions.Folders.Length);
        var store = new StorageScanStore(fixture.Path); store.Save(result);
        Assert.Equal(6, store.Load()!.AccessRestrictions!.Folders.Sum(f => f.Count));
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Contains("Access restricted: 6", vm.CoverageTotals); Assert.Contains("Other errors: 2", vm.CoverageTotals);
        Assert.Equal(2, vm.Failures.Count); Assert.DoesNotContain(vm.Failures, i => i.Category == StorageScanIssueCategory.AccessDenied);
        Assert.Equal(6, vm.AccessRestrictedSamples.Count); Assert.True(vm.HasAccessRestrictions);
        Assert.Equal(6, vm.AccessRestrictedFolders.Sum(f => f.Count)); Assert.All(vm.AccessRestrictedFolders, f => Assert.Equal("Failures", f.CountKind));
        Assert.True(vm.ShowCoverageWarning); vm.Close();
        using var output = new MemoryStream(); await new ApplicationDiagnosticReader(fixture.Path).ExportFailuresAsync(output);
        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"AccessRestricted\":6", text); Assert.Contains("\"OtherErrors\":2", text);
        Assert.Contains("\"FailedEntries\":8", text); Assert.Contains("\"NativeCode\":5", text); Assert.Contains("\"NativeCode\":1117", text);
        Assert.DoesNotContain(JsonSerializer.Serialize(@"C:\scan")[1..^1], text);
    }

    [Fact]
    public async Task LegacyTotalsAreExactButFolderRowsCountOnlyRetainedSamplesWithoutRewriting()
    {
        using var fixture = new Fixture();
        var result = new StorageScanSnapshot(1, @"C:\scan", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, 1000, 0, 936, 0, false, [], Enumerable.Range(0, 10).Select(i => Denied(@"C:\scan\folder\sample-" + i)).ToArray())
            { FailureClassificationVersion = 1, IssueCounts = [new(StorageScanIssueCategory.AccessDenied, 935), new(StorageScanIssueCategory.NativeIoFailure, 1)] };
        var store = new StorageScanStore(fixture.Path); store.Save(result);
        byte[] before = File.ReadAllBytes(fixture.Baseline);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Contains("Access restricted: 935", vm.CoverageTotals); Assert.Contains("Other errors: 1", vm.CoverageTotals);
        Assert.Contains("retained samples only (10 shown)", vm.AccessRestrictionSummary);
        Assert.Contains("Rescan", vm.AccessRestrictionSummary);
        Assert.Equal(10, Assert.Single(vm.AccessRestrictedFolders).Count);
        Assert.Equal("Retained samples", vm.AccessRestrictedFolders[0].CountKind);
        Assert.True(vm.ShowCoverageWarning); vm.Close();
        Assert.Null(store.Load()!.AccessRestrictions);
        Assert.Equal(before, File.ReadAllBytes(fixture.Baseline));
    }

    [Fact]
    public async Task AnEntirelyRestrictedScanStillShowsPartialCoverageAndNoOtherErrors()
    {
        using var fixture = new Fixture();
        var result = new StorageScanner(new Files(onlyRestrictions: true)).Scan(@"C:\scan");
        Assert.Equal(result.FailedEntries, result.AccessRestrictedEntries); Assert.Equal(0, result.OtherErrorEntries);
        new StorageScanStore(fixture.Path).Save(result);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.False(vm.ShowCoverageWarning); Assert.True(vm.HasCoverageNote); Assert.True(vm.HasAccessRestrictions); Assert.Empty(vm.Failures); vm.Close();
    }

    [Fact]
    public async Task RawSamplesWithoutCategoryCountsDoNotInventAFullRestrictionTotal()
    {
        using var fixture = new Fixture();
        var result = new StorageScanSnapshot(1, @"C:\scan", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, 1000, 0, 935, 0, false, [], [Denied(@"C:\scan\sample")]);
        new StorageScanStore(fixture.Path).Save(result);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Contains("Failed: unknown", vm.CoverageTotals);
        Assert.Contains("full access-restricted total is unknown", vm.AccessRestrictionSummary);
        Assert.Equal("Retained samples", Assert.Single(vm.AccessRestrictedFolders).CountKind);
        Assert.True(vm.ShowCoverageWarning); vm.Close();
    }

    [Fact]
    public void InvalidGroupingVersionsBoundsPathsAndTotalsPreserveTheSavedBaseline()
    {
        using var fixture = new Fixture(); var store = new StorageScanStore(fixture.Path);
        var result = new StorageScanner(new Files()).Scan(@"C:\scan"); store.Save(result);
        byte[] before = File.ReadAllBytes(fixture.Baseline);
        foreach (var invalid in new StorageAccessRestrictionSummary[] {
            new(99, [], 6), new(1, null!, 6), new(1, [], -1), new(1, [], 5),
            new(1, [new(@"C:\scan\folder", 0)], 6), new(1, [new(@"C:\scan\folder", -1)], 7),
            new(1, [new(@"C:\scan\folder", 3), new(@"c:\SCAN\FOLDER", 3)], 0),
            new(1, [new(@"C:\scan-lookalike", 6)], 0), new(1, [new(@"C:\scan\unused\..\folder", 6)], 0),
            new(1, [new(@"C:\scan\one\two\three\four\five\six", 6)], 0),
            new(1, [new(@"C:\scan\A", 1), new(@"C:\scan\B", 4), new(@"C:\scan\locked", 1)], 0),
            new(1, [new(@"C:\scan\A", 6)], 0),
            new(1, [new("relative", 6)], 0), new(1, [new(@"C:\scan\" + new string('x', 1024), 6)], 0),
            new(1, Enumerable.Range(0, 129).Select(i => new StorageAccessFolderCount(@"C:\scan\folder-" + i, 1)).ToArray(), 0) })
            Assert.Throws<InvalidDataException>(() => store.Save(result with { AccessRestrictions = invalid }));
        Assert.Throws<InvalidDataException>(() => store.Save(result with { IssueCounts = null }));
        Assert.Equal(before, File.ReadAllBytes(fixture.Baseline));
        File.WriteAllText(fixture.Baseline, JsonSerializer.Serialize(result with { AccessRestrictions = new(99, [], 6) }));
        byte[] unsupported = File.ReadAllBytes(fixture.Baseline);
        Assert.Throws<InvalidDataException>(() => store.Load()); Assert.Equal(unsupported, File.ReadAllBytes(fixture.Baseline));
    }

    [Fact]
    public void SuccessfulScanPersistsAnEmptyGroupingInsteadOfClaimingLegacyUnknown()
    {
        using var fixture = new Fixture();
        var result = new StorageScanner(new EmptyFiles()).Scan(@"C:\scan");
        var store = new StorageScanStore(fixture.Path); store.Save(result);
        Assert.True(result.CoverageComplete); Assert.Equal(0, result.AccessRestrictedEntries);
        Assert.Empty(store.Load()!.AccessRestrictions!.Folders);
    }

    private static StorageScanIssue Denied(string path, ScanIoOperation operation = ScanIoOperation.ReadMetadata) =>
        StorageScanIssue.Failure(path, operation, new Win32Exception(5));
    private sealed class Files(bool onlyRestrictions = false) : IStorageScanFileSystem
    {
        public StorageFileMetadata Read(string path)
        {
            if (path.Contains("denied", StringComparison.Ordinal)) throw new Win32Exception(5);
            if (path.EndsWith("io-error", StringComparison.Ordinal)) throw new Win32Exception(1117);
            if (path.EndsWith("sharing", StringComparison.Ordinal)) throw new Win32Exception(32);
            return new(true, path.EndsWith("excluded", StringComparison.Ordinal), "", 0, 0);
        }
        public IEnumerable<string> Enumerate(string path)
        {
            if (path.EndsWith("locked", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
            if (path != @"C:\scan") return [];
            string[] restrictions = [@"C:\scan\A\denied-1", @"C:\scan\A\denied-2", @"C:\scan\A\denied-3",
                @"C:\scan\B\denied-1", @"C:\scan\B\denied-2", @"C:\scan\locked"];
            return onlyRestrictions ? restrictions : restrictions.Concat([@"C:\scan\io-error", @"C:\scan\sharing", @"C:\scan\excluded"]);
        }
    }
    private sealed class EmptyFiles : IStorageScanFileSystem
    {
        public StorageFileMetadata Read(string path) => new(true, false, "", 0, 0);
        public IEnumerable<string> Enumerate(string directory) => [];
    }
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-access-test-" + Guid.NewGuid().ToString("N"));
        public string Baseline => System.IO.Path.Combine(Path, "Storage", "latest-scan.json");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
