using System.Diagnostics;
using System.Text.Json.Nodes;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ReviewedCleanupTests
{
    [Fact]
    public async Task NativePreviewAppliesAgeRetentionExclusionsAndDoesNotDelete()
    {
        using var fixture = new Fixture();
        string old = fixture.File("old.log", 30), newest = fixture.File("new.log", 1);
        string excluded = fixture.File("keep.log", 40);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "nested"));
        string nested = Path.Combine(fixture.Root, "nested", "old.log"); File.WriteAllText(nested, "untouched");
        var rule = fixture.Rule with { RetainNewest = 1, Exclusions = ["keep.log"] };
        var preview = await fixture.Service.PreviewAsync(rule);
        Assert.True(preview.Complete);
        Assert.Equal(old, Assert.Single(preview.Candidates).Path);
        Assert.All(new[] { old, newest, excluded, nested }, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task NativeReviewedRunDeletesOnlySelectedFileAndCannotReplay()
    {
        using var fixture = new Fixture();
        string selected = fixture.File("selected.log"), retained = fixture.File("retained.log");
        var preview = await fixture.Service.PreviewAsync(fixture.Rule);
        var run = await fixture.Service.ExecuteReviewedAsync(preview.Id, [selected]);
        Assert.False(File.Exists(selected)); Assert.True(File.Exists(retained));
        Assert.Equal("Deleted permanently", Assert.Single(run.Items).Outcome);
        Assert.NotNull(run.AvailableBefore); Assert.NotNull(run.AvailableAfter);
        Assert.Equal(run.Id, new CleanupStore(fixture.Data).LoadLastRun()!.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteReviewedAsync(preview.Id, [retained]));
    }

    [Fact]
    public async Task NativeChangedAndLockedFilesSurvive()
    {
        using var fixture = new Fixture();
        string changed = fixture.File("changed.log"), locked = fixture.File("locked.log");
        var preview = await fixture.Service.PreviewAsync(fixture.Rule);
        string replacement = fixture.File("replacement.tmp");
        File.Move(replacement, changed, true);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var run = await fixture.Service.ExecuteReviewedAsync(preview.Id, [changed, locked]);
        Assert.All(run.Items, i => Assert.Equal("Skipped", i.Outcome));
        Assert.True(File.Exists(changed)); Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task NativeAncestorJunctionReplacementCannotRedirectDeletion()
    {
        using var fixture = new Fixture();
        string original = fixture.File("old.log");
        var preview = await fixture.Service.PreviewAsync(fixture.Rule);
        string target = Path.Combine(fixture.Base, "outside"); Directory.CreateDirectory(target);
        string valuable = Path.Combine(target, "old.log"); File.WriteAllText(valuable, "preserve");
        Directory.Move(fixture.Root, fixture.Root + "-original");
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", fixture.Root, target }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!) { Assert.True(process.WaitForExit(10000)); Assert.Equal(0, process.ExitCode); }
        try
        {
            var run = await fixture.Service.ExecuteReviewedAsync(preview.Id, [original]);
            Assert.Equal("Skipped", Assert.Single(run.Items).Outcome);
            Assert.Equal("preserve", File.ReadAllText(valuable));
            Assert.True(File.Exists(Path.Combine(fixture.Root + "-original", "old.log")));
        }
        finally { Directory.Delete(fixture.Root); }
    }

    [Fact]
    public async Task ProtectedLocationsAndBroadPatternsAreRejected()
    {
        using var fixture = new Fixture();
        foreach (string root in new[] { fixture.Data, fixture.Base, Path.GetPathRoot(fixture.Root)!, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PreviewAsync(fixture.Rule with { Root = root }));
        Assert.Throws<ArgumentException>(() => (fixture.Rule with { Pattern = "*.*" }).Validate());
        Assert.Throws<ArgumentException>(() => (fixture.Rule with { Pattern = "..\\*.log" }).Validate());
        Assert.Throws<ArgumentException>(() => (fixture.Rule with { MinimumAgeDays = 0 }).Validate());
    }

    [Fact]
    public async Task UnknownApplicationGuardBlocksPreviewAndNewRunningGuardStopsRun()
    {
        using var fixture = new Fixture();
        bool absent = false;
        var service = new ReviewedCleanupService(fixture.Data, guard: (_, _) => Task.FromResult(absent));
        string path = fixture.File("old.log");
        await Assert.ThrowsAsync<IOException>(() => service.PreviewAsync(fixture.Rule));
        absent = true;
        var preview = await service.PreviewAsync(fixture.Rule);
        absent = false;
        var run = await service.ExecuteReviewedAsync(preview.Id, [path]);
        Assert.Contains("guard", run.Problem);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task IncompleteCoverageAndForgedSelectionsNeverDelete()
    {
        using var fixture = new Fixture();
        var fake = new FakeFiles(fixture.Root) { FailRead = true };
        var service = new ReviewedCleanupService(fixture.Data, fake);
        var preview = await service.PreviewAsync(fixture.Rule);
        Assert.False(preview.Complete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteReviewedAsync(preview.Id, [fake.First.Path]));
        fake.FailRead = false;
        preview = await service.PreviewAsync(fixture.Rule);
        preview.Candidates[0] = fake.First with { Path = Path.Combine(fixture.Root, "forged.log") };
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteReviewedAsync(preview.Id, [preview.Candidates[0].Path]));
        Assert.Empty(fake.Deleted);
    }

    [Fact]
    public async Task ExpiredPreviewAndCanceledRunCannotGainFurtherDeletionAuthority()
    {
        using var fixture = new Fixture();
        var clock = new FakeClock(); var fake = new FakeFiles(fixture.Root);
        var service = new ReviewedCleanupService(fixture.Data, fake, clock: clock);
        var preview = await service.PreviewAsync(fixture.Rule);
        clock.Elapsed += TimeSpan.FromMinutes(11);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteReviewedAsync(preview.Id, [fake.First.Path]));
        Assert.Empty(fake.Deleted);
        using var cancel = new CancellationTokenSource();
        fake.AfterDelete = _ => cancel.Cancel();
        preview = await service.PreviewAsync(fixture.Rule);
        var run = await service.ExecuteReviewedAsync(preview.Id, preview.Candidates.Select(f => f.Path).ToArray(), cancel.Token);
        Assert.Single(fake.Deleted);
        Assert.Contains("Canceled", run.Problem);
        Assert.Contains(run.Items, i => i.Outcome == "Not attempted");
    }

    [Fact]
    public async Task MissingDurableAuditPreventsDeletionAndPostDeleteAuditFailureStopsRun()
    {
        using var fixture = new Fixture(); var fake = new FakeFiles(fixture.Root);
        var service = new ReviewedCleanupService(fixture.Data, fake);
        var preview = await service.PreviewAsync(fixture.Rule);
        Directory.CreateDirectory(Path.Combine(fixture.Data, "Storage"));
        string audit = Path.Combine(fixture.Data, "Storage", "cleanup-last-run.json");
        File.WriteAllText(audit, "{bad");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => service.ExecuteReviewedAsync(preview.Id, [fake.First.Path]));
        Assert.Empty(fake.Deleted);
        File.Delete(audit); // Only this test's intentionally damaged evidence.
        FileStream? held = null;
        fake.AfterDelete = _ => held = new FileStream(audit, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            preview = await service.PreviewAsync(fixture.Rule);
            var failure = await Record.ExceptionAsync(() => service.ExecuteReviewedAsync(preview.Id, preview.Candidates.Select(f => f.Path).ToArray()));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Single(fake.Deleted);
        }
        finally { held?.Dispose(); }
        var persisted = new CleanupStore(fixture.Data).LoadLastRun()!;
        Assert.Contains(persisted.Items, i => i.Outcome.StartsWith("Pending", StringComparison.Ordinal));
        Assert.Contains(persisted.Items, i => i.Outcome == "Not attempted");
    }

    [Fact]
    public void RulesRoundTripRejectStaleWritersAndCannotEnableAutomaticDeletion()
    {
        using var fixture = new Fixture(); var store = new CleanupStore(fixture.Data);
        string path = fixture.File("untouched.log");
        var saved = store.SaveRules(0, [fixture.Rule]);
        Assert.Equal(fixture.Rule.Root, Assert.Single(store.LoadRules().Rules).Root);
        Assert.Throws<InvalidOperationException>(() => store.SaveRules(0, []));
        store.SaveRules(saved.Revision, saved.Rules);
        string config = Path.Combine(fixture.Data, "Storage", "cleanup-rules.json");
        var json = JsonNode.Parse(File.ReadAllText(config))!; json["AutomaticDeletionEnabled"] = true;
        File.WriteAllText(config, json.ToJsonString());
        Assert.Throws<System.Text.Json.JsonException>(() => store.LoadRules());
        Assert.Throws<System.Text.Json.JsonException>(() => store.SaveRules(2, []));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task PreviewEnforcesCountBytesAndNewestRetention()
    {
        using var fixture = new Fixture(); var fake = new FakeFiles(fixture.Root);
        var service = new ReviewedCleanupService(fixture.Data, fake);
        var preview = await service.PreviewAsync(fixture.Rule with { MaximumFiles = 1, MaximumBytes = 10 });
        Assert.Single(preview.Candidates);
        Assert.Contains(preview.Issues, i => i.Reason.Contains("limit", StringComparison.Ordinal));
        preview = await service.PreviewAsync(fixture.Rule with { RetainNewest = 1 });
        Assert.Single(preview.Candidates);
    }

    private sealed class FakeClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Elapsed;
    }
    private sealed class FakeFiles
        : ICleanupFileSystem
    {
        public CleanupFile First { get; }
        public CleanupFile Second { get; }
        public bool FailRead { get; set; }
        public Action<string>? AfterDelete { get; set; }
        public List<string> Deleted { get; } = [];
        public FakeFiles(string root)
        {
            First = new(Path.Combine(root, "first.log"), "one", 10, 4096, DateTimeOffset.UtcNow.AddDays(-40), 1, 1, 0);
            Second = new(Path.Combine(root, "second.log"), "two", 10, 4096, DateTimeOffset.UtcNow.AddDays(-30), 1, 1, 0);
        }
        public IEnumerable<string> Enumerate(string directory) => [First.Path, Second.Path];
        public CleanupFile ReadCleanup(string path) => FailRead ? throw new IOException("Unknown test metadata") : path == First.Path ? First : Second;
        public void DeleteVerified(CleanupFile expected) { Deleted.Add(expected.Path); AfterDelete?.Invoke(expected.Path); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), "relight-cleanup-test-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Base, "files");
        public string Data => Path.Combine(Base, "data");
        public CleanupRule Rule => new(Guid.NewGuid(), "Test old logs", Root, "*.log", [], RetainNewest: 0);
        public ReviewedCleanupService Service { get; }
        public Fixture() { Directory.CreateDirectory(Root); Service = new(Data); }
        public string File(string name, int age = 30)
        {
            string path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, "disposable test");
            System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
            return path;
        }
        public void Dispose() => Directory.Delete(Base, true);
    }
}
