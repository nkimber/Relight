using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class AutomaticCleanupTests
{
    [Fact]
    public async Task OffByDefaultAndApprovalRequiresBothLowSpaceAndFullInterval()
    {
        using var fixture = new Fixture(); using var gate = new SemaphoreSlim(1);
        var service = fixture.Service();
        Assert.Contains("off", (await service.PulseAsync(gate)).Status);
        Assert.Empty(fixture.Files.Deleted);
        fixture.Approve();
        await service.PulseAsync(gate);
        fixture.Clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Null((await service.PulseAsync(gate)).Run);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var pulse = await service.PulseAsync(gate);
        Assert.NotNull(pulse.Run);
        Assert.Equal("Automatic", pulse.Run!.Origin);
        Assert.Single(fixture.Files.Deleted);
        Assert.Null((await service.PulseAsync(gate)).Run);
    }

    [Fact]
    public async Task UnknownOrSufficientCapacityAndBusyWorkerNeverDispatch()
    {
        using var fixture = new Fixture(); using var gate = new SemaphoreSlim(1);
        fixture.Approve(); var service = fixture.Service(); await service.PulseAsync(gate);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Available = 100L * 1_073_741_824;
        Assert.Null((await service.PulseAsync(gate)).Run);
        fixture.Available = null;
        Assert.Contains("unknown", (await service.PulseAsync(gate)).Status);
        fixture.Available = 0; await gate.WaitAsync();
        Assert.Contains("deferred", (await service.PulseAsync(gate)).Status);
        gate.Release(); Assert.Empty(fixture.Files.Deleted);
    }

    [Fact]
    public async Task ReservationSurvivesRestartAndClockJumpAloneCannotAccelerate()
    {
        using var fixture = new Fixture(); using var gate = new SemaphoreSlim(1);
        fixture.Approve(); var service = fixture.Service(); await service.PulseAsync(gate);
        fixture.Clock.WallOffset += TimeSpan.FromDays(10);
        Assert.Null((await service.PulseAsync(gate)).Run);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull((await service.PulseAsync(gate)).Run);
        var persisted = fixture.Store.LoadRules();
        Assert.True(Assert.Single(persisted.AutomaticApprovals).NextEligibleUtc > fixture.Clock.GetUtcNow());
        Assert.Contains("owned", (await fixture.Service().PulseAsync(gate)).Status);
        service.Dispose();
        var restarted = fixture.Service();
        Assert.Null((await restarted.PulseAsync(gate)).Run);
        fixture.Clock.WallOffset += TimeSpan.FromDays(10);
        Assert.Null((await restarted.PulseAsync(gate)).Run);
        Assert.Single(fixture.Files.Deleted);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull((await restarted.PulseAsync(gate)).Run);
        Assert.Equal(2, fixture.Files.Deleted.Count);
    }

    [Fact]
    public async Task EditRevokesAllApprovalsAndDisablePreventsFurtherRuns()
    {
        using var fixture = new Fixture(); using var gate = new SemaphoreSlim(1);
        fixture.Approve(); var old = Assert.Single(fixture.Store.LoadRules().AutomaticApprovals);
        var book = fixture.Store.LoadRules();
        fixture.Store.SaveRules(book.Revision, [fixture.Rule with { MinimumAgeDays = 40 }]);
        Assert.False(fixture.Store.LoadRules().AutomaticEnabled);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.AcquireAutomaticAuthorization(old.Id, old.RuleFingerprint));
        fixture.Store.DisableAutomatic();
        fixture.Clock.Advance(TimeSpan.FromDays(7));
        Assert.Null((await fixture.Service().PulseAsync(gate)).Run);
        Assert.Empty(fixture.Files.Deleted);
    }

    [Fact]
    public async Task DisableDuringBatchStopsAfterAlreadyDispatchedFile()
    {
        using var fixture = new Fixture(maximumFiles: 2); using var gate = new SemaphoreSlim(1);
        fixture.Approve();
        bool disabled = false;
        using var service = new AutomaticCleanupService(fixture.Data, fixture.Clock, _ => 0,
            () => new ReviewedCleanupService(fixture.Data, fixture.Files, guard: (_, _) =>
            {
                if (fixture.Files.Deleted.Count > 0 && !disabled) { fixture.Store.DisableAutomatic(); disabled = true; }
                return Task.FromResult(true);
            }, clock: fixture.Clock));
        await service.PulseAsync(gate); fixture.Clock.Advance(TimeSpan.FromHours(1));
        var pulse = await service.PulseAsync(gate);
        Assert.Single(fixture.Files.Deleted);
        Assert.Contains("revoked", pulse.Run!.Problem);
        Assert.Contains(pulse.Run.Items, i => i.Outcome == "Not attempted");
    }

    [Fact]
    public async Task CorruptApprovalDataCannotBecomeFreshAutomaticAuthority()
    {
        using var fixture = new Fixture(); using var gate = new SemaphoreSlim(1);
        fixture.Approve();
        File.WriteAllText(Path.Combine(fixture.Data, "Storage", "cleanup-rules.json"), "{broken");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => fixture.Service().PulseAsync(gate));
        Assert.Empty(fixture.Files.Deleted);
    }

    [Fact]
    public async Task ImmediateStopCancelsRemainingFilesAndHistoryIsBounded()
    {
        using var fixture = new Fixture(maximumFiles: 2); using var gate = new SemaphoreSlim(1);
        fixture.Approve(); var service = fixture.Service();
        fixture.Files.AfterDelete = service.Stop;
        await service.PulseAsync(gate); fixture.Clock.Advance(TimeSpan.FromHours(1));
        var pulse = await service.PulseAsync(gate);
        Assert.Single(fixture.Files.Deleted);
        Assert.Contains("Canceled", pulse.Run!.Problem);
        Assert.Contains("stopped", (await service.PulseAsync(gate)).Status);
        Assert.Equal("Automatic", Assert.Single(fixture.Store.LoadHistory()).Origin);
        for (int i = 0; i < 105; i++)
            fixture.Store.SaveRun(new(Guid.NewGuid(), fixture.Rule.Id, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), [], 1, 1));
        Assert.Equal(100, fixture.Store.LoadHistory().Length);
    }

    [Fact]
    public async Task NativeApprovedAutomaticRunDeletesOnlyDisposableEligibleFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "relight-native-auto-" + Guid.NewGuid().ToString("N"));
        string files = Path.Combine(root, "files"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(files);
        try
        {
            string old = Path.Combine(files, "old.log"), excluded = Path.Combine(files, "keep.log");
            File.WriteAllText(old, "disposable"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            File.WriteAllText(excluded, "preserve"); File.SetLastWriteTimeUtc(excluded, DateTime.UtcNow.AddDays(-30));
            var clock = new FakeClock(); var store = new CleanupStore(data, clock);
            var rule = new CleanupRule(Guid.NewGuid(), "Native disposable test", files, "*.log", ["keep.log"], RetainNewest: 0);
            var book = store.SaveRules(0, [rule]); store.ApproveAutomatic(book.Revision, rule, 1, 1_073_741_824);
            using var service = new AutomaticCleanupService(data, clock, _ => 0);
            using var gate = new SemaphoreSlim(1);
            await service.PulseAsync(gate); clock.Advance(TimeSpan.FromHours(1));
            var pulse = await service.PulseAsync(gate);
            Assert.NotNull(pulse.Run); Assert.False(File.Exists(old)); Assert.True(File.Exists(excluded));
            Assert.Equal("Deleted permanently", Assert.Single(pulse.Run!.Items).Outcome);
            Assert.Equal(rule.Fingerprint(), pulse.Run.RuleSnapshot!.Fingerprint());
            Assert.Equal(rule.Name, Assert.Single(store.LoadHistory()).RuleName);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private TimeSpan _elapsed;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => _start + _elapsed + WallOffset;
        public void Advance(TimeSpan elapsed) => _elapsed += elapsed;
    }
    private sealed class FakeFiles(string root) : ICleanupFileSystem
    {
        public List<string> Deleted { get; } = [];
        public Action? AfterDelete { get; set; }
        public IEnumerable<string> Enumerate(string directory) => new[] { "a.log", "b.log" }
            .Select(n => Path.Combine(root, n)).Where(p => !Deleted.Contains(p));
        public CleanupFile ReadCleanup(string path) => new(path, path, 10, 4096, DateTimeOffset.UtcNow.AddDays(-30), 1, 1, 0);
        public void DeleteVerified(CleanupFile expected) { Deleted.Add(expected.Path); AfterDelete?.Invoke(); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "relight-auto-test-" + Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(_base, "data");
        public FakeClock Clock { get; } = new();
        public CleanupStore Store { get; }
        public FakeFiles Files { get; }
        public CleanupRule Rule { get; }
        public long? Available { get; set; }
        private readonly List<AutomaticCleanupService> _services = [];
        public Fixture(int maximumFiles = 1)
        {
            Directory.CreateDirectory(_base);
            Rule = new(Guid.NewGuid(), "Approved disposable logs", Path.Combine(_base, "files"), "*.log", [],
                RetainNewest: 0, MaximumFiles: maximumFiles);
            Files = new(Rule.Root); Store = new(Data, Clock); Store.SaveRules(0, [Rule]);
            Available = 0;
        }
        public void Approve() => Store.ApproveAutomatic(Store.LoadRules().Revision, Rule, 1, 1_073_741_824);
        public AutomaticCleanupService Service()
        {
            var service = new AutomaticCleanupService(Data, Clock, _ => Available,
                () => new ReviewedCleanupService(Data, Files, clock: Clock));
            _services.Add(service); return service;
        }
        public void Dispose() { foreach (var service in _services) service.Dispose(); Directory.Delete(_base, true); }
    }
}
