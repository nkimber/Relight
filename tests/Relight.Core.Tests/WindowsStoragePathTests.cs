using System.Diagnostics;
using Relight.Core;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class WindowsStoragePathTests
{
    [Theory]
    [InlineData(260)]
    [InlineData(600)]
    public void NativeScanMeasuresLongPathsFromShortAndLongRoots(int minimumLength)
    {
        using var fixture = new Fixture();
        string root = fixture.LongDirectory(fixture.Content, minimumLength);
        string first = Path.Combine(root, "é文.bin");
        File.WriteAllBytes(first, [1, 2, 3]);
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        string second = Path.Combine(nested, "second.bin");
        File.WriteAllBytes(second, [4, 5, 6, 7]);

        foreach (string selectedRoot in new[] { fixture.Content, root })
        {
            var result = new StorageScanner().Scan(selectedRoot);
            Assert.True(result.CoverageComplete, string.Join("; ", result.Issues.Select(i => i.Reason)));
            Assert.Equal(2, result.FilesMeasured);
            Assert.Equal(0, result.SkippedEntries);
            Assert.Contains(result.Entries, e => e.Path == first && e.LogicalBytes == 3);
            Assert.Contains(result.Entries, e => e.Path == second && e.LogicalBytes == 4);
            Assert.Equal(7, Assert.Single(result.Entries, e => e.Path == selectedRoot).LogicalBytes);
            Assert.DoesNotContain(result.Entries, e => e.Path.StartsWith(@"\\?\", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NativeLongPathReadNormalizesSegmentsBeforeOpening()
    {
        using var fixture = new Fixture();
        string root = fixture.LongDirectory(fixture.Content, 600);
        string path = Path.Combine(root, "sample.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var files = new WindowsStorageScanFileSystem();

        var actual = files.Read(Path.Combine(root, "unused", "..", "sample.bin"));
        Assert.Equal(files.Read(path), actual);
        Assert.Equal(3, actual.LogicalBytes);
    }

    [Fact]
    public void NativeLongPathJunctionAncestorIsRejected()
    {
        using var fixture = new Fixture();
        string root = fixture.LongDirectory(fixture.Content, 600);
        File.WriteAllBytes(Path.Combine(root, "preserved.bin"), [1]);
        string junction = Path.Combine(fixture.Base, "junction");
        fixture.Junction(junction, fixture.Content);
        try
        {
            string alias = Path.Combine(junction, Path.GetRelativePath(fixture.Content, root));
            Assert.Throws<IOException>(() => new WindowsStorageScanFileSystem().Read(alias));
            var result = new StorageScanner().Scan(alias);
            Assert.False(result.CoverageComplete);
            Assert.Equal(0, result.FilesMeasured);
            Assert.NotEmpty(result.Issues);
            Assert.True(File.Exists(Path.Combine(root, "preserved.bin")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task NativeReviewedCleanupSupportsLongPathsAndPreservesChangedAndUnselectedFiles()
    {
        using var fixture = new Fixture();
        string root = fixture.LongDirectory(fixture.Content, 600);
        string selected = fixture.OldLog(root, "selected.log");
        string changed = fixture.OldLog(root, "changed.log");
        string unselected = fixture.OldLog(root, "unselected.log");
        var service = new ReviewedCleanupService(fixture.Data);
        var rule = new CleanupRule(Guid.NewGuid(), "Disposable long paths", root, "*.log", [], RetainNewest: 0);
        var preview = await service.PreviewAsync(rule);
        Assert.True(preview.Complete);
        Assert.Equal(3, preview.Candidates.Length);
        File.AppendAllText(changed, " changed after preview");

        var run = await service.ExecuteReviewedAsync(preview.Id, [selected, changed]);
        Assert.Null(run.Problem);
        Assert.Equal("Deleted permanently", Assert.Single(run.Items, i => i.Path == selected).Outcome);
        Assert.Equal("Skipped", Assert.Single(run.Items, i => i.Path == changed).Outcome);
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(unselected));
    }

    [Fact]
    public async Task NativeLongPathJunctionReplacementCannotRedirectCleanup()
    {
        using var fixture = new Fixture();
        string root = fixture.LongDirectory(fixture.Content, 600);
        string original = fixture.OldLog(root, "old.log");
        var service = new ReviewedCleanupService(fixture.Data);
        var rule = new CleanupRule(Guid.NewGuid(), "Disposable long paths", root, "*.log", [], RetainNewest: 0);
        var preview = await service.PreviewAsync(rule);
        Assert.True(preview.Complete);
        Assert.Single(preview.Candidates);
        string relativeRoot = Path.GetRelativePath(fixture.Content, root);
        string outside = Path.Combine(fixture.Base, "outside");
        string outsideRoot = Path.Combine(outside, relativeRoot);
        Directory.CreateDirectory(outsideRoot);
        string valuable = fixture.OldLog(outsideRoot, "old.log");
        string retainedRoot = fixture.Content + "-original";
        Directory.Move(fixture.Content, retainedRoot);
        fixture.Junction(fixture.Content, outside);
        try
        {
            var run = await service.ExecuteReviewedAsync(preview.Id, [original]);
            Assert.Equal("Skipped", Assert.Single(run.Items).Outcome);
            Assert.Equal("disposable test", File.ReadAllText(valuable));
            Assert.True(File.Exists(Path.Combine(retainedRoot, relativeRoot, "old.log")));
        }
        finally { Directory.Delete(fixture.Content); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "relight-storage-path-test-" + Guid.NewGuid().ToString("N"));
        public string Content => Path.Combine(Base, "files");
        public string Data => Path.Combine(Base, "data");
        public Fixture() => Directory.CreateDirectory(Content);
        public string LongDirectory(string root, int minimumLength)
        {
            while (root.Length < minimumLength) root = Path.Combine(root, "é文-" + new string('x', 60));
            Directory.CreateDirectory(root);
            return root;
        }
        public string OldLog(string root, string name)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, "disposable test");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            return path;
        }
        public void Junction(string path, string target)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        public void Dispose()
        {
            string path = Path.GetFullPath(Base);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(path, true);
        }
    }
}
