using System.ComponentModel;
using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;

namespace Relight.Core.Tests;

public sealed class StorageScanPresentationTests
{
    [Theory]
    [InlineData("complete", false, "Scan completed.")]
    [InlineData("excluded", false, "Scan completed with exclusions.")]
    [InlineData("access", false, "Scan completed with access restrictions.")]
    [InlineData("protected", false, "Scan completed with access restrictions.")]
    [InlineData("mixed-restrictions", false, "Scan completed with exclusions and access restrictions.")]
    [InlineData("old-folder-groups", false, "Scan completed with exclusions and access restrictions.")]
    [InlineData("missing", true, "SCAN COMPLETED WITH ERRORS")]
    [InlineData("sharing", true, "SCAN COMPLETED WITH ERRORS")]
    [InlineData("metadata", true, "SCAN COMPLETED WITH ERRORS")]
    [InlineData("enumeration", true, "SCAN COMPLETED WITH ERRORS")]
    [InlineData("native", true, "SCAN COMPLETED WITH ERRORS")]
    [InlineData("overflow", true, "SCAN COMPLETED WITH ERRORS")]
    [InlineData("entry-limit", true, "ENTRY LIMIT REACHED")]
    [InlineData("depth-limit", true, "DIRECTORY DEPTH REACHED")]
    [InlineData("unfinished", true, "SCAN UNFINISHED")]
    [InlineData("legacy-gaps", true, "COVERAGE UNKNOWN")]
    [InlineData("legacy-protected-split", true, "COVERAGE UNKNOWN")]
    [InlineData("legacy-complete", false, "Scan completed.")]
    public async Task WarningProminenceUsesKnownErrorsLimitsTraversalAndUnknownCoverage(string scenario, bool warn, string expected)
    {
        using var fixture = new Fixture();
        var snapshot = Scenario(scenario);
        new StorageScanStore(fixture.Path).Save(snapshot);
        byte[] before = File.ReadAllBytes(fixture.Baseline);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Equal(warn, vm.ShowCoverageWarning);
        Assert.Contains(expected, warn ? vm.CoverageWarningTitle : vm.Status);
        if (scenario == "depth-limit") Assert.Contains("Scan completed with directory depth limits", vm.Status);
        if (!warn && !snapshot.CoverageComplete)
        {
            Assert.False(snapshot.CoverageComplete); Assert.True(vm.HasCoverageNote);
            Assert.Contains("not measured", vm.CoverageNote); Assert.DoesNotContain("PARTIAL", vm.FilesHeading);
            Assert.DoesNotContain("Partial scan", vm.Status);
        }
        if (warn) Assert.Contains("Larger files may be missing", vm.CoverageWarning);
        Assert.Equal(before, File.ReadAllBytes(fixture.Baseline)); vm.Close();
    }

    [Fact]
    public void CombinedWarningListsEveryCondition()
    {
        var scan = Snapshot((StorageScanIssueCategory.DepthLimit, 2), (StorageScanIssueCategory.NativeIoFailure, 3)) with
            { EntryLimitReached = true, TraversalComplete = false };
        var presentation = StorageScanPresentation.For(scan);
        Assert.True(presentation.Warn); Assert.Contains("ENTRY LIMIT", presentation.WarningTitle);
        Assert.Contains("depth was limited 2 times", presentation.Explanation);
        Assert.Contains("3 other read errors", presentation.Explanation);
    }

    [Theory]
    [InlineData("mixed-restrictions", false)]
    [InlineData("native", true)]
    public async Task WaitingAndCancellationPreservePriorPresentation(string scenario, bool warn)
    {
        using var fixture = new Fixture(); new StorageScanStore(fixture.Path).Save(Scenario(scenario));
        using var gate = new SemaphoreSlim(0, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        string totals = vm.CoverageTotals; string warning = vm.CoverageWarningTitle;
        Task pending = vm.ScanAsync(); Assert.True(vm.IsScanning); Assert.False(vm.ShowCoverageWarning); Assert.False(vm.ShowScanContent);
        vm.Cancel(); await pending;
        Assert.False(vm.IsScanning); Assert.Equal(warn, vm.ShowCoverageWarning);
        Assert.Equal(totals, vm.CoverageTotals); Assert.Equal(warning, vm.CoverageWarningTitle);
        Assert.Contains("canceled", vm.Status); vm.Close();
    }

    [Theory]
    [InlineData(2, "Missing or changed path", "check whether the exact path still exists")]
    [InlineData(3, "Missing or changed path", "check whether the exact path still exists")]
    [InlineData(32, "File or folder in use", "Let the owning operation finish")]
    [InlineData(33, "File or folder in use", "Let the owning operation finish")]
    [InlineData(23, "Windows device or data-read error", "check storage health")]
    [InlineData(1117, "Windows device or data-read error", "check storage health")]
    [InlineData(1, "Metadata request rejected by Windows", "adapter compatibility")]
    [InlineData(50, "Metadata request rejected by Windows", "adapter compatibility")]
    [InlineData(87, "Metadata request rejected by Windows", "adapter compatibility")]
    [InlineData(206, "Windows path length error", "long-path handling")]
    [InlineData(999, "Windows I/O error", "scan-failures logs")]
    public void NativeErrorDetailsPreserveEvidenceAndUseCodeSpecificNextSteps(int code, string expected, string nextStep)
    {
        var source = StorageScanIssue.Failure(@"C:\scan\affected.file", ScanIoOperation.ReadMetadata, new Win32Exception(code));
        var detail = StorageScanErrorDetail.For(source);
        Assert.Equal(expected, detail.Error); Assert.Contains(nextStep, detail.NextSteps);
        Assert.Equal(source.Reason, detail.Reason); Assert.Equal(code.ToString(), detail.WindowsCode);
        Assert.Contains(source.Path, detail.Report); Assert.Contains(source.Reason, detail.Report);
        Assert.Contains("Read metadata", detail.Report); Assert.Contains("HRESULT: 0x", detail.Report);
        Assert.Contains("Suggested next steps:", detail.Report);
    }

    [Theory]
    [InlineData(StorageScanIssueCategory.MetadataUnavailable, "safeguard")]
    [InlineData(StorageScanIssueCategory.EnumerationFailure, "scan it directly")]
    [InlineData(StorageScanIssueCategory.SizeOverflow, "smaller subfolders")]
    public void ManagedErrorGuidancePreservesUnknownCodes(StorageScanIssueCategory category, string nextStep)
    {
        var detail = StorageScanErrorDetail.For(new(@"C:\scan\folder", "recorded scanner message")
            { Category = category, Operation = ScanIoOperation.EnumerateDirectory });
        Assert.Equal("Not recorded", detail.WindowsCode); Assert.Equal("Not recorded", detail.HResult);
        Assert.Contains("List folder contents", detail.Report); Assert.Contains(nextStep, detail.NextSteps);
    }

    [Fact]
    public void RootValidationGuidanceExplainsSelectingAnAvailableFolder()
    {
        var detail = StorageScanErrorDetail.For(StorageScanIssue.Failure(@"C:\scan", ScanIoOperation.RootValidation, new IOException("Select a folder, not a file.")));
        Assert.Equal("Validate scan root", detail.Operation);
        Assert.Contains("Select an existing direct folder", detail.NextSteps);
        var device = StorageScanErrorDetail.For(StorageScanIssue.Failure(@"C:\scan", ScanIoOperation.RootValidation, new Win32Exception(1117)));
        Assert.Contains("check storage health", device.NextSteps);
    }

    [Fact]
    public async Task ExactErrorCountsAndSelectedCopyTextStaySeparateFromBoundedSamples()
    {
        using var fixture = new Fixture();
        var scan = Snapshot((StorageScanIssueCategory.MissingOrChanged, 200), (StorageScanIssueCategory.NativeIoFailure, 100),
            (StorageScanIssueCategory.AccessDenied, 20), (StorageScanIssueCategory.ProtectedSystemAccess, 30)) with
        { Issues = [StorageScanIssue.Failure(@"C:\scan\missing", ScanIoOperation.ReadMetadata, new Win32Exception(3)),
            StorageScanIssue.Failure(@"C:\scan\io-error", ScanIoOperation.EnumerateDirectory, new Win32Exception(1117))] };
        new StorageScanStore(fixture.Path).Save(scan);
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Equal(300, vm.ErrorCounts.Sum(c => c.Count)); Assert.Equal(2, vm.ErrorDetails.Count);
        Assert.Contains("300 other read errors", vm.ErrorSummary); Assert.Contains("2 first/recent", vm.ErrorSummary);
        Assert.All(vm.ErrorCounts, c => Assert.False(string.IsNullOrWhiteSpace(c.NextSteps)));
        vm.SelectedError = vm.ErrorDetails[1]; Assert.True(vm.HasSelectedError);
        Assert.Contains(@"C:\scan\io-error", vm.SelectedErrorReport); Assert.Contains("1117", vm.SelectedErrorReport);
        Assert.Contains("List folder contents", vm.SelectedErrorReport); Assert.Contains("check storage health", vm.SelectedErrorReport);
        vm.SelectedError = null; Assert.False(vm.HasSelectedError); Assert.Empty(vm.SelectedErrorReport); vm.Close();
    }

    [Fact]
    public async Task CountsWithoutRetainedPathDetailsRemainActionableAndUnknownPathsAreExplicit()
    {
        using var fixture = new Fixture(); new StorageScanStore(fixture.Path).Save(Scenario("native"));
        using var gate = new SemaphoreSlim(1, 1);
        var vm = new StorageScanViewModel(fixture.Path, gate); await vm.LoadAsync();
        Assert.Single(vm.ErrorCounts); Assert.Empty(vm.ErrorDetails); Assert.False(vm.HasSelectedError);
        Assert.Contains("No path examples", vm.ErrorSummary); Assert.Contains("Rescan", vm.ErrorSummary); vm.Close();
    }

    private static StorageScanSnapshot Scenario(string scenario) => scenario switch
    {
        "complete" => Snapshot(), "excluded" => Snapshot((StorageScanIssueCategory.ReparseOrOffline, 5)),
        "access" => Snapshot((StorageScanIssueCategory.AccessDenied, 5)), "protected" => Snapshot((StorageScanIssueCategory.ProtectedSystemAccess, 5)),
        "mixed-restrictions" or "old-folder-groups" => Snapshot((StorageScanIssueCategory.ReparseOrOffline, 5), (StorageScanIssueCategory.AccessDenied, 6), (StorageScanIssueCategory.ProtectedSystemAccess, 7)),
        "missing" => Snapshot((StorageScanIssueCategory.MissingOrChanged, 1)), "sharing" => Snapshot((StorageScanIssueCategory.SharingViolation, 1)),
        "metadata" => Snapshot((StorageScanIssueCategory.MetadataUnavailable, 1)), "enumeration" => Snapshot((StorageScanIssueCategory.EnumerationFailure, 1)),
        "native" => Snapshot((StorageScanIssueCategory.NativeIoFailure, 1)), "overflow" => Snapshot((StorageScanIssueCategory.SizeOverflow, 1)),
        "entry-limit" => Snapshot() with { EntryLimitReached = true, TraversalComplete = false },
        "depth-limit" => Snapshot((StorageScanIssueCategory.DepthLimit, 1)), "unfinished" => Snapshot() with { TraversalComplete = false },
        "legacy-gaps" => Snapshot((StorageScanIssueCategory.AccessDenied, 2)) with { IssueCounts = null, FailureClassificationVersion = null },
        "legacy-protected-split" => Snapshot((StorageScanIssueCategory.AccessDenied, 2)) with { FailureClassificationVersion = null },
        "legacy-complete" => Snapshot() with { IssueCounts = null, FailureClassificationVersion = null },
        _ => throw new ArgumentException(scenario)
    };
    private static StorageScanSnapshot Snapshot(params (StorageScanIssueCategory Category, int Count)[] counts) =>
        new(1, @"C:\scan", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, 1000, 1, counts.Sum(c => c.Count), 0, false, [], [])
        { IssueCounts = counts.Select(c => new StorageScanIssueCount(c.Category, c.Count)).ToArray(), FailureClassificationVersion = 1 };
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-presentation-test-" + Guid.NewGuid().ToString("N"));
        public string Baseline => System.IO.Path.Combine(Path, "Storage", "latest-scan.json");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
