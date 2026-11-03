using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ProtectedSystemScanTests
{
    private static ProtectedSystemScanLocations Locations => new(@"D:\OS", @"E:\SharedData");

    [Theory]
    [InlineData(@"E:\SharedData\Microsoft\Crypto\Keys\key")]
    [InlineData(@"e:\SHAREDDATA\Microsoft\Crypto\Keys\")]
    [InlineData(@"E:\SharedData\Microsoft\Crypto\SystemKeys\key")]
    [InlineData(@"E:\SharedData\Microsoft\Crypto\RSA\MachineKeys\key")]
    [InlineData(@"E:\SharedData\Microsoft\Crypto\DSS\MachineKeys\key")]
    [InlineData(@"D:\OS\System32\config\SAM")]
    [InlineData(@"D:\$Recycle.Bin\user\file")]
    [InlineData(@"D:\System Volume Information\file")]
    [InlineData(@"D:\OS\Temp")]
    [InlineData(@"D:\pagefile.sys")]
    [InlineData(@"D:\hiberfil.sys")]
    [InlineData(@"D:\swapfile.sys")]
    [InlineData(@"D:\DumpStack.log")]
    [InlineData(@"D:\DumpStack.log.tmp")]
    public void KnownLocationsKeepFailedKindCodesAndOperations(string path)
    {
        var error = new Win32Exception(5);
        var original = StorageScanIssue.Failure(path, ScanIoOperation.ReadMetadata, error);
        var issue = Locations.Classify(original);
        Assert.Equal(StorageScanIssueCategory.ProtectedSystemAccess, issue.Category);
        Assert.Equal(StorageScanIssueKind.Failed, issue.Kind);
        Assert.Equal(original with { Category = issue.Category }, issue);
    }

    [Theory]
    [InlineData(@"E:\SharedData\Microsoft\Crypto\Keys-backup\key")]
    [InlineData(@"E:\SharedData\Microsoft\Crypto\Keys\..\Keys-backup\key")]
    [InlineData(@"E:\SharedData-copy\Microsoft\Crypto\Keys\key")]
    [InlineData(@"D:\Users\person\Microsoft\Crypto\Keys\key")]
    [InlineData(@"D:\OS\Tasks\custom.job")]
    [InlineData(@"D:\OS\Temp\user-created.log")]
    [InlineData(@"D:\Users\person\pagefile.sys")]
    [InlineData(@"C:\ProgramData\Microsoft\Crypto\Keys\key")]
    [InlineData(@"relative\Keys\key")]
    [InlineData(@"\\server\share\Microsoft\Crypto\Keys\key")]
    public void OtherLocationsAndLookalikesKeepTheirOriginalFailure(string path)
    {
        var issue = StorageScanIssue.Failure(path, ScanIoOperation.ReadMetadata, new Win32Exception(5));
        Assert.Same(issue, Locations.Classify(issue));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(87, false)]
    [InlineData(1117, false)]
    [InlineData(5, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    public void OnlyAccessAndSharingErrorsReceiveTheLocationLabel(int code, bool protectedSystem)
    {
        var issue = StorageScanIssue.Failure(@"E:\SharedData\Microsoft\Crypto\Keys\key", ScanIoOperation.EnumerateDirectory, new Win32Exception(code));
        Assert.Equal(protectedSystem, Locations.Classify(issue).Category == StorageScanIssueCategory.ProtectedSystemAccess);
    }

    [Fact]
    public async Task ScanPersistencePresentationAndFailureLogPreserveTotalsAndCoverage()
    {
        using var fixture = new Fixture();
        await using var diagnostics = new ApplicationDiagnostics(fixture.Path, _ => { }, new("test", "Release", ".NET", "Windows", "X64", 1, 1), startWorker: false);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Crypto\Keys");
        var scan = diagnostics.BeginScan(root, 100)!;
        var files = new Files(root);
        var result = new StorageScanner(files).Scan(root, diagnostics: scan);
        scan.End(DiagnosticScanStage.Completed, result); diagnostics.Log.Drain();
        Assert.Equal(2, result.FailedEntries); Assert.Equal(1, result.ProtectedSystemFailedEntries); Assert.Equal(1, result.OtherFailedEntries);
        Assert.Equal(0, result.ExcludedEntries); Assert.False(result.CoverageComplete); Assert.Equal(2, files.ChildReads);
        var store = new StorageScanStore(fixture.Path); store.Save(result);
        Assert.Equal(1, store.Load()!.ProtectedSystemFailedEntries);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Contains("Failed: 2", vm.CoverageTotals); Assert.Contains("Protected system: 1", vm.CoverageTotals); Assert.Contains("Other failures: 1", vm.CoverageTotals);
        Assert.True(vm.ShowCoverageWarning); Assert.Single(vm.Failures); Assert.Single(vm.ProtectedFailures); Assert.True(vm.HasProtectedFailures);
        Assert.Equal(StorageScanIssueCategory.MissingOrChanged, vm.Failures[0].Category); vm.Close();
        using var output = new MemoryStream(); await new ApplicationDiagnosticReader(fixture.Path).ExportFailuresAsync(output);
        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("ProtectedSystemAccess", text); Assert.Contains("\"ProtectedSystemFailed\":1", text); Assert.Contains("\"OtherFailed\":1", text);
        Assert.Contains("\"FailedEntries\":2", text); Assert.Contains("\"NativeCode\":5", text); Assert.Contains("\"NativeCode\":3", text);
        Assert.DoesNotContain(JsonSerializer.Serialize(root)[1..^1], text);
    }

    [Fact]
    public async Task AnEntirelyProtectedFailureSetStillShowsIncompleteCoverage()
    {
        using var fixture = new Fixture();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Crypto\Keys");
        var result = new StorageScanner(new Files(root, includeMissing: false)).Scan(root);
        Assert.Equal(1, result.FailedEntries); Assert.Equal(1, result.ProtectedSystemFailedEntries); Assert.Equal(0, result.OtherFailedEntries);
        Assert.False(result.CoverageComplete);
        new StorageScanStore(fixture.Path).Save(result);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.True(vm.ShowCoverageWarning); Assert.Empty(vm.Failures); Assert.Single(vm.ProtectedFailures); vm.Close();
    }

    [Fact]
    public async Task PriorClassifiedResultsKeepTheProtectedSplitUnknownAndUnsupportedVersionsPreserveEvidence()
    {
        using var fixture = new Fixture(); var store = new StorageScanStore(fixture.Path);
        var error = new Win32Exception(5);
        var older = new StorageScanSnapshot(1, @"C:\scan", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, 2, 0, 1, 0, false, [], [StorageScanIssue.Failure(@"C:\scan\file", ScanIoOperation.ReadMetadata, error)])
            { IssueCounts = [new(StorageScanIssueCategory.AccessDenied, 1)] };
        store.Save(older); Assert.Null(store.Load()!.ProtectedSystemFailedEntries); Assert.Null(store.Load()!.OtherFailedEntries);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Contains("split: unknown", vm.CoverageTotals); Assert.Single(vm.Failures); Assert.Empty(vm.ProtectedFailures); Assert.True(vm.ShowCoverageWarning); vm.Close();
        byte[] before = File.ReadAllBytes(Path.Combine(fixture.Path, "Storage", "latest-scan.json"));
        Assert.Throws<InvalidDataException>(() => store.Save(older with { FailureClassificationVersion = 99 }));
        Assert.Throws<InvalidDataException>(() => store.Save(older with { IssueCounts = [new(StorageScanIssueCategory.ProtectedSystemAccess, 1)] }));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Path, "Storage", "latest-scan.json")));
    }

    private sealed class Files(string root, bool includeMissing = true) : IStorageScanFileSystem
    {
        public int ChildReads { get; private set; }
        public StorageFileMetadata Read(string path)
        {
            if (path == root + @"\denied") { ChildReads++; throw new Win32Exception(5); }
            if (path == root + @"\missing") { ChildReads++; throw new Win32Exception(3); }
            return new(true, false, "", 0, 0);
        }
        public IEnumerable<string> Enumerate(string path) => path != root ? [] : includeMissing
            ? [root + @"\denied", root + @"\missing"] : [root + @"\denied"];
    }
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-protected-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
