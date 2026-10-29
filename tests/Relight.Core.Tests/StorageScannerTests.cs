using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class StorageScannerTests
{
    [Fact]
    public void AggregatesDescendantsDeduplicatesAllocationAndReportsExclusions()
    {
        var files = new FakeFiles();
        files.Children[@"C:\scan"] = [@"C:\scan\a", @"C:\scan\b", @"C:\scan\link", @"C:\scan\denied"];
        files.Children[@"C:\scan\a"] = [@"C:\scan\a\file"];
        files.Children[@"C:\scan\b"] = [@"C:\scan\b\alias"];
        files.Metadata[@"C:\scan\a\file"] = new(false, false, "shared-file", 5000, 8192);
        files.Metadata[@"C:\scan\b\alias"] = new(false, false, "shared-file", 5000, 8192);
        files.Metadata[@"C:\scan\link"] = new(true, true, "", 0, 0);
        files.Failures.Add(@"C:\scan\denied");
        var result = new StorageScanner(files).Scan(@"C:\scan");
        var root = Assert.Single(result.Entries, e => e.Path == @"C:\scan");
        Assert.Equal(10000, root.LogicalBytes);
        Assert.Equal(8192, root.AllocatedBytes);
        Assert.False(root.CoverageComplete);
        Assert.False(result.CoverageComplete);
        Assert.Equal(2, result.SkippedEntries);
        Assert.Equal(1, result.HardLinkAliases);
        Assert.DoesNotContain(@"C:\scan\link", files.Enumerated);
        Assert.Contains(result.Issues, i => i.Path == @"C:\scan\denied");
    }

    [Fact]
    public void PartialEnumerationKeepsAlreadyMeasuredSizes()
    {
        var files = new FakeFiles();
        files.Metadata[@"C:\scan\one"] = new(false, false, "1", 42, 4096);
        files.PartialEnumeration = true;
        var result = new StorageScanner(files).Scan(@"C:\scan");
        Assert.Equal(42, Assert.Single(result.Entries, e => e.Path == @"C:\scan").LogicalBytes);
        Assert.False(result.CoverageComplete);
        Assert.Single(result.Issues);
    }

    [Fact]
    public void EntryLimitAndRetainedResultsAreBounded()
    {
        var files = new FakeFiles { GeneratedFiles = 5000 };
        var scanner = new StorageScanner(files);
        var limited = scanner.Scan(@"C:\scan", maximumEntries: 20);
        Assert.True(limited.EntryLimitReached);
        Assert.Equal(20, limited.EntriesVisited);
        Assert.False(limited.CoverageComplete);
        var result = scanner.Scan(@"C:\scan");
        Assert.True(result.CoverageComplete);
        Assert.Equal(100, result.Entries.Count(e => e.Kind == StorageScanEntryKind.File));
        Assert.Equal(5000, result.FilesMeasured);
        Assert.True(result.Entries.Length <= StorageScanSnapshot.MaximumRetainedEntries);
    }

    [Fact]
    public void ReparseAncestorPreventsTraversal()
    {
        var files = new FakeFiles();
        files.Metadata[@"C:\scan"] = new(true, true, "", 0, 0);
        var result = new StorageScanner(files).Scan(@"C:\scan\nested");
        Assert.False(result.CoverageComplete);
        Assert.Empty(files.Enumerated);
        Assert.Single(result.Issues);
        Assert.Throws<ArgumentException>(() => new StorageScanner(files).Scan("relative-folder"));
    }

    [Fact]
    public void RaisedBudgetReachesLargeFileBeyondOldCapAndRoundTripsActualLimit()
    {
        var files = new FakeFiles { GeneratedFiles = 100001 };
        string large = @"C:\scan\generated-100000";
        files.Metadata[large] = new(false, false, "docker-like-disk", 500L * 1073741824, 400L * 1073741824);
        var scanner = new StorageScanner(files);
        var partial = scanner.Scan(@"C:\scan");
        Assert.True(partial.EntryLimitReached);
        Assert.DoesNotContain(partial.Entries, e => e.Path == large);
        var complete = scanner.Scan(@"C:\scan", maximumEntries: 100002);
        Assert.False(complete.EntryLimitReached); Assert.True(complete.CoverageComplete);
        Assert.Equal(100002, complete.EntriesVisited);
        Assert.Equal(large, complete.Entries.First(e => e.Kind == StorageScanEntryKind.File).Path);
        using var data = new TestDirectory(); var store = new StorageScanStore(data.Path);
        store.Save(complete); Assert.Equal(100002, store.Load()!.EntryLimit);
    }

    [Fact]
    public async Task LimitWarningSurvivesReloadAndChangingBudgetUntilCompleteRescan()
    {
        using var data = new TestDirectory();
        string files = Path.Combine(data.Path, "files"); Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "a.bin"), "disposable");
        File.WriteAllText(Path.Combine(files, "b.bin"), "disposable");
        using var gate = new SemaphoreSlim(1); int limit = 2;
        var model = new StorageScanViewModel(Path.Combine(data.Path, "data"), gate, () => limit) { Root = files };
        await model.ScanAsync();
        Assert.True(model.EntryLimitReached); Assert.True(model.HasCoverageWarning);
        Assert.Contains("ENTRY LIMIT REACHED", model.CoverageWarningTitle);
        Assert.Contains("Larger files may be missing", model.CoverageWarning);
        Assert.Contains("PARTIAL", model.FilesHeading); Assert.Contains("PARTIAL", model.FoldersHeading);
        var loaded = new StorageScanViewModel(Path.Combine(data.Path, "data"), gate); await loaded.LoadAsync();
        Assert.True(loaded.HasCoverageWarning); Assert.True(loaded.EntryLimitReached); loaded.Close();
        limit = 10; Assert.True(model.HasCoverageWarning);
        await model.ScanAsync(); Assert.False(model.HasCoverageWarning); Assert.False(model.EntryLimitReached);
        Assert.Equal(2, model.ScanVersion); Assert.DoesNotContain("PARTIAL", model.FilesHeading); model.Close();
    }

    [Fact]
    public void LegacySnapshotDefaultsToOldBudgetAndInvalidLargerCounterIsRejected()
    {
        using var data = new TestDirectory(); var store = new StorageScanStore(data.Path);
        var snapshot = new StorageScanner(new FakeFiles()).Scan(@"C:\scan"); store.Save(snapshot);
        string path = Path.Combine(data.Path, "Storage", "latest-scan.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json.AsObject().Remove("EntryLimit"); File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(100000, store.Load()!.EntryLimit);
        json["EntriesVisited"] = 100001; File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public async Task SkippedCoverageGetsPersistentWarningWithoutLimitStopClaim()
    {
        using var data = new TestDirectory(); var fake = new FakeFiles(); fake.Failures.Add(@"C:\scan");
        new StorageScanStore(data.Path).Save(new StorageScanner(fake).Scan(@"C:\scan"));
        using var gate = new SemaphoreSlim(1); var model = new StorageScanViewModel(data.Path, gate);
        await model.LoadAsync(); Assert.True(model.HasCoverageWarning); Assert.False(model.EntryLimitReached);
        Assert.Contains("COVERAGE INCOMPLETE", model.CoverageWarningTitle); model.Close();
    }

    [Fact]
    public void CancellationStopsTraversalWithoutReturningASnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var files = new FakeFiles { GeneratedFiles = 5000,
            OnRead = path => { if (path.EndsWith("10", StringComparison.Ordinal)) cancellation.Cancel(); } };
        Assert.Throws<OperationCanceledException>(() => new StorageScanner(files).Scan(@"C:\scan", cancellation.Token));
        Assert.True(files.Reads < 100);
    }

    [Fact]
    public void CoverageIssueListAndDepthAreBounded()
    {
        var files = new FakeFiles();
        files.Children[@"C:\scan"] = Enumerable.Range(0, 150).Select(i => @"C:\scan\denied-" + i).ToArray();
        foreach (string child in files.Children[@"C:\scan"]) files.Failures.Add(child);
        var result = new StorageScanner(files).Scan(@"C:\scan");
        Assert.Equal(150, result.SkippedEntries);
        Assert.Equal(100, result.Issues.Length);
        var deep = new FakeFiles();
        string path = @"C:\scan";
        for (int i = 0; i < 140; i++)
        {
            string child = path + "\\d";
            deep.Children[path] = [child];
            path = child;
        }
        var depth = new StorageScanner(deep).Scan(@"C:\scan");
        Assert.Equal(129, depth.EntriesVisited);
        Assert.Contains(depth.Issues, i => i.Reason.Contains("depth", StringComparison.Ordinal));
        Assert.False(depth.CoverageComplete);
    }

    [Fact]
    public void GrowthRequiresSameRootAndComparableCoverage()
    {
        var files = new FakeFiles();
        files.Children[@"C:\scan"] = [@"C:\scan\one"];
        files.Metadata[@"C:\scan\one"] = new(false, false, "1", 10, 4096);
        var scanner = new StorageScanner(files);
        var before = scanner.Scan(@"C:\scan");
        files.Metadata[@"C:\scan\one"] = new(false, false, "1", 50, 4096);
        var after = scanner.Scan(@"C:\scan");
        Assert.All(StorageScanComparison.Compare(after, before), g => Assert.Equal(40, g.LogicalGrowthBytes));
        Assert.Equal(2, StorageScanComparison.Compare(after, before).Count);
        Assert.Empty(StorageScanComparison.Compare(after, before with { Root = @"C:\other" }));
        Assert.Empty(StorageScanComparison.Compare(after, before with
        { Entries = before.Entries.Select(e => e with { CoverageComplete = false }).ToArray() }));
        Assert.Empty(StorageScanComparison.Compare(after, before with { Entries = [] }));
    }

    [Fact]
    public void StoreIsBoundedRoundTripsAndPreservesDamagedEvidence()
    {
        using var directory = new TestDirectory();
        var snapshot = new StorageScanner(new FakeFiles()).Scan(@"C:\scan");
        var store = new StorageScanStore(directory.Path);
        Assert.Null(store.Load());
        Assert.Null(store.Save(snapshot));
        Assert.Equal(snapshot.Root, store.Load()!.Root);
        Assert.NotNull(store.Save(snapshot));
        Assert.Throws<InvalidDataException>(() => store.Save(snapshot with { SchemaVersion = 2 }));
        var volume = snapshot with { Root = @"C:\", Entries = [new(@"C:\file.bin", StorageScanEntryKind.File, 1, 4096, true)] };
        store.Save(volume);
        Assert.Equal(@"C:\", store.Load()!.Root);
        string path = System.IO.Path.Combine(directory.Path, "Storage", "latest-scan.json");
        File.WriteAllText(path, "{broken");
        Assert.Throws<System.Text.Json.JsonException>(() => store.Save(snapshot));
        Assert.Equal("{broken", File.ReadAllText(path));
        File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public async Task CanceledQueuedScanRetainsSavedBaseline()
    {
        using var directory = new TestDirectory();
        using var gate = new SemaphoreSlim(0, 1);
        var baseline = new StorageScanner(new FakeFiles()).Scan(@"C:\scan");
        new StorageScanStore(directory.Path).Save(baseline);
        var model = new StorageScanViewModel(directory.Path, gate);
        Task scan = model.ScanAsync();
        Assert.True(model.IsScanning);
        model.Cancel();
        await scan;
        Assert.True(model.IsIdle);
        Assert.Contains("canceled", model.Status);
        Assert.Equal(baseline.FinishedUtc, new StorageScanStore(directory.Path).Load()!.FinishedUtc);
    }

    [Fact]
    public void NativeScanMeasuresHardLinksAndSkipsJunction()
    {
        using var directory = new TestDirectory();
        string content = System.IO.Path.Combine(directory.Path, "content");
        Directory.CreateDirectory(content);
        string original = System.IO.Path.Combine(content, "original.bin");
        File.WriteAllBytes(original, new byte[5000]);
        string alias = System.IO.Path.Combine(content, "alias.bin");
        if (!CreateHardLink(alias, original, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        string outside = System.IO.Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(System.IO.Path.Combine(outside, "excluded.bin"), new byte[10000]);
        string junction = System.IO.Path.Combine(content, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction); start.ArgumentList.Add(outside);
        using (var process = Process.Start(start)!)
        {
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var result = new StorageScanner().Scan(content);
            Assert.Equal(2, result.FilesMeasured);
            Assert.Equal(1, result.HardLinkAliases);
            var root = Assert.Single(result.Entries, e => e.Path == content);
            Assert.Equal(10000, root.LogicalBytes);
            Assert.InRange(root.AllocatedBytes, 5000, 16384);
            Assert.Contains(result.Issues, i => i.Path == junction);
            Assert.DoesNotContain(result.Entries, e => e.Path.EndsWith("excluded.bin", StringComparison.Ordinal));
            Assert.Equal(5000, new FileInfo(original).Length);
        }
        finally { Directory.Delete(junction); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);

    [Fact]
    public void NativeSparseFileUsesActualAllocatedSpace()
    {
        using var directory = new TestDirectory();
        string path = System.IO.Path.Combine(directory.Path, "sparse.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            if (!DeviceIoControl(stream.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            stream.SetLength(1024 * 1024);
        }
        var result = new StorageScanner().Scan(directory.Path);
        var file = Assert.Single(result.Entries, e => e.Kind == StorageScanEntryKind.File);
        Assert.Equal(1024 * 1024, file.LogicalBytes);
        Assert.Equal(0, file.AllocatedBytes);
        Assert.True(result.CoverageComplete);
    }

    [Fact]
    public void NativeEnumerationSpansBuffersAndDoesNotBlockDirectoryRename()
    {
        using var directory = new TestDirectory();
        string root = System.IO.Path.Combine(directory.Path, "many");
        Directory.CreateDirectory(root);
        for (int i = 0; i < 800; i++)
            File.WriteAllBytes(System.IO.Path.Combine(root, $"{i:D4}-é文-{new string('x', 80)}.bin"), [1]);
        var result = new StorageScanner().Scan(root);
        Assert.True(result.CoverageComplete, string.Join("; ", result.Issues.Select(i => i.Reason)));
        Assert.Equal(800, result.FilesMeasured);
        Assert.Equal(800, Assert.Single(result.Entries, e => e.Path == root).LogicalBytes);
        using var entries = new WindowsStorageScanFileSystem().Enumerate(root).GetEnumerator();
        Assert.True(entries.MoveNext());
        string renamed = root + "-renamed";
        Directory.Move(root, renamed);
        Assert.True(Directory.Exists(renamed));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        uint code, IntPtr input, int inputLength, IntPtr output, int outputLength,
        out int returned, IntPtr overlapped);

    private sealed class FakeFiles : IStorageScanFileSystem
    {
        public Dictionary<string, StorageFileMetadata> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Enumerated { get; } = [];
        public Action<string>? OnRead { get; init; }
        public bool PartialEnumeration { get; set; }
        public int GeneratedFiles { get; init; }
        public int Reads { get; private set; }
        public StorageFileMetadata Read(string path)
        {
            Reads++;
            OnRead?.Invoke(path);
            if (Failures.Contains(path)) throw new UnauthorizedAccessException("Test denied");
            if (Metadata.TryGetValue(path, out var metadata)) return metadata;
            if (path.StartsWith(@"C:\scan\generated-", StringComparison.Ordinal))
                return new(false, false, path, 123, 4096);
            return new(true, false, "", 0, 0);
        }
        public IEnumerable<string> Enumerate(string directory)
        {
            Enumerated.Add(directory);
            if (PartialEnumeration) { yield return @"C:\scan\one"; throw new IOException("Enumeration interrupted"); }
            if (GeneratedFiles > 0)
                for (int i = 0; i < GeneratedFiles; i++) yield return @"C:\scan\generated-" + i;
            else if (Children.TryGetValue(directory, out var children))
                foreach (string child in children) yield return child;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relight-scan-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
