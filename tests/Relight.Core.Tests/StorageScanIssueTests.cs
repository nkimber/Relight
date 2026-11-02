using System.ComponentModel;
using System.Text.Json.Nodes;
using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class StorageScanIssueTests
{
    [Theory]
    [InlineData(5, StorageScanIssueCategory.AccessDenied)]
    [InlineData(2, StorageScanIssueCategory.MissingOrChanged)]
    [InlineData(3, StorageScanIssueCategory.MissingOrChanged)]
    [InlineData(32, StorageScanIssueCategory.SharingViolation)]
    [InlineData(33, StorageScanIssueCategory.SharingViolation)]
    [InlineData(87, StorageScanIssueCategory.NativeIoFailure)]
    public void NativeCodesAndOperationArePreserved(int code, StorageScanIssueCategory expected)
    {
        var error = new Win32Exception(code);
        var issue = StorageScanIssue.Failure(@"C:\scan\file", ScanIoOperation.ReadMetadata, error);
        Assert.Equal(expected, issue.Category);
        Assert.Equal(StorageScanIssueKind.Failed, issue.Kind);
        Assert.Equal(code, issue.NativeErrorCode);
        Assert.Equal(error.HResult, issue.HResult);
        Assert.Equal(ScanIoOperation.ReadMetadata, issue.Operation);
    }

    [Fact]
    public void ManagedFailuresAndDepthLimitsAreDistinct()
    {
        Assert.Equal(StorageScanIssueCategory.SharingViolation, StorageScanIssue.Failure(@"C:\scan", ScanIoOperation.ReadMetadata,
            new IOException("locked", unchecked((int)0x80070020))).Category);
        Assert.Equal(StorageScanIssueCategory.EnumerationFailure, StorageScanIssue.Failure(@"C:\scan", ScanIoOperation.EnumerateDirectory, new IOException()).Category);
        Assert.Equal(StorageScanIssueCategory.SizeOverflow, StorageScanIssue.Failure(@"C:\scan", ScanIoOperation.Aggregate, new OverflowException()).Category);
        Assert.Equal(StorageScanIssueKind.Limited, StorageScanIssue.KindOf(StorageScanIssueCategory.DepthLimit));
    }

    [Fact]
    public void AllCategoriesKeepExactCountsWithinTheSharedSampleBound()
    {
        var collector = new StorageScanIssueCollector();
        var categories = Enum.GetValues<StorageScanIssueCategory>().Where(c => c != StorageScanIssueCategory.Unknown).ToArray();
        foreach (var category in categories)
            for (int i = 0; i < 1000; i++) collector.Add(new(@"C:\scan\sample-" + i, "test") { Category = category });
        Assert.Equal(categories.Length * 1000, collector.Total);
        Assert.All(collector.Counts(), c => Assert.Equal(1000, c.Count));
        Assert.InRange(collector.Samples().Length, 1, StorageScanSnapshot.MaximumIssues);
        Assert.All(categories, category => Assert.Contains(collector.Samples(), s => s.Category == category && s.Path.EndsWith("sample-999", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExclusionsCannotConsumeFailureSamplesAndCountsSurviveReload()
    {
        using var fixture = new Fixture();
        var result = new StorageScanner(new Files()).Scan(@"C:\scan");
        Assert.Equal(1301, result.SkippedEntries);
        Assert.Equal(1000, result.ExcludedEntries);
        Assert.Equal(301, result.FailedEntries);
        Assert.Equal(0, result.LimitedEntries);
        Assert.False(result.CoverageComplete);
        Assert.Contains(result.Issues, i => i.Path.EndsWith("denied-0", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Path.EndsWith("denied-299", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Category == StorageScanIssueCategory.MissingOrChanged && i.NativeErrorCode == 3);
        Assert.InRange(result.Issues.Length, 1, StorageScanSnapshot.MaximumIssues);
        new StorageScanStore(fixture.Path).Save(result);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate);
        await vm.LoadAsync();
        Assert.Contains("Excluded: 1,000", vm.CoverageTotals);
        Assert.Contains("Failed: 301", vm.CoverageTotals);
        Assert.All(vm.Failures, i => Assert.Equal(StorageScanIssueKind.Failed, i.Kind));
        Assert.All(vm.Exclusions, i => Assert.Equal(StorageScanIssueKind.Excluded, i.Kind));
        Assert.Empty(vm.UnclassifiedIssues);
        Assert.Equal(301, vm.IssueCounts.Where(c => c.Kind == "Failed").Sum(c => c.Count));
        vm.Close();
    }

    [Fact]
    public async Task LegacySplitRemainsUnknownAndInvalidCountsPreserveTheBaseline()
    {
        using var fixture = new Fixture();
        var store = new StorageScanStore(fixture.Path);
        var legacy = new StorageScanSnapshot(1, @"C:\scan", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, 1000, 500, 400, 0, false, [], [new(@"C:\scan\sample", "Access denied")]);
        store.Save(legacy);
        string path = System.IO.Path.Combine(fixture.Path, "Storage", "latest-scan.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (string property in new[] { "IssueCounts", "ExcludedEntries", "FailedEntries", "LimitedEntries" }) json.AsObject().Remove(property);
        foreach (string property in new[] { "Category", "Operation", "NativeErrorCode", "HResult", "Kind" }) json["Issues"]![0]!.AsObject().Remove(property);
        File.WriteAllText(path, json.ToJsonString());
        byte[] before = File.ReadAllBytes(path);
        var loaded = store.Load()!;
        Assert.Null(loaded.FailedEntries); Assert.Null(loaded.ExcludedEntries);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate);
        await vm.LoadAsync();
        Assert.Contains("Failed: unknown", vm.CoverageTotals);
        Assert.Empty(vm.Failures); Assert.Single(vm.UnclassifiedIssues); Assert.True(vm.ShowCoverageWarning);
        vm.Close();
        foreach (var counts in new StorageScanIssueCount[][] {
            [new(StorageScanIssueCategory.AccessDenied, 399)],
            [new(StorageScanIssueCategory.AccessDenied, -1)],
            [new(StorageScanIssueCategory.AccessDenied, 200), new(StorageScanIssueCategory.AccessDenied, 200)],
            [new((StorageScanIssueCategory)999, 400)] })
            Assert.Throws<InvalidDataException>(() => store.Save(loaded with { IssueCounts = counts }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private sealed class Files : IStorageScanFileSystem
    {
        public StorageFileMetadata Read(string path)
        {
            if (path.Contains("excluded-", StringComparison.Ordinal)) return new(true, true, "", 0, 0);
            if (path.Contains("denied-", StringComparison.Ordinal)) throw new UnauthorizedAccessException("test denial");
            if (path.EndsWith("missing", StringComparison.Ordinal)) throw new Win32Exception(3);
            return new(true, false, "", 0, 0);
        }
        public IEnumerable<string> Enumerate(string directory) => Enumerable.Range(0, 1000).Select(i => @"C:\scan\excluded-" + i)
            .Concat(Enumerable.Range(0, 300).Select(i => @"C:\scan\denied-" + i)).Append(@"C:\scan\missing");
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-issue-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
