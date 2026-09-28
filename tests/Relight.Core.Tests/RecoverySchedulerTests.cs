using Relight.Core;
using Relight.Engine;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class RecoverySchedulerTests
{
    private static RecoveryPolicy AutoPolicy => RecoveryPolicy.Default with
    {
        StartAutomaticallyWhenInitiallyAbsent = true,
        NormalPollInterval = TimeSpan.FromSeconds(120),
        RetryDelay = TimeSpan.FromSeconds(5)
    };

    [Fact]
    public async Task One_blocked_discovery_does_not_hold_the_shared_scheduler()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var blocked = new BlockingDiscovery();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(a, ProfileCoordinator.CreateNew(a, AutoPolicy, store,
            blocked, new CountingLauncher(), clock), AutoPolicy);
        scheduler.Add(b, ProfileCoordinator.CreateNew(b, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), new CountingLauncher(), clock), AutoPolicy);

        IReadOnlyDictionary<Guid, Task> started = scheduler.Pulse();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await started[b].WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(started[a].IsCompleted);
        Assert.NotNull(scheduler.GetLast(b)?.Result);
        Assert.Empty(scheduler.Pulse());
        blocked.Release.TrySetResult();
        await started[a].WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Absence_and_retry_deadlines_wake_before_the_normal_poll_interval()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(id, ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock), AutoPolicy);

        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(1);
        Assert.Empty(scheduler.Pulse());
        clock.Elapsed = TimeSpan.FromSeconds(2);
        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(6);
        Assert.Empty(scheduler.Pulse());
        clock.Elapsed = TimeSpan.FromSeconds(7);
        await Assert.Single(scheduler.Pulse()).Value;
        Assert.Equal(1, launcher.Dispatches);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Remove_cancels_an_inflight_poll_before_disposing_its_coordinator()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var blocked = new BlockingDiscovery();
        Guid id = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(id, ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            blocked, new CountingLauncher(), clock), AutoPolicy);
        Task inFlight = Assert.Single(scheduler.Pulse()).Value;
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await scheduler.RemoveAsync(id).WaitAsync(TimeSpan.FromSeconds(3));
        await inFlight;
        Assert.Empty(scheduler.Pulse());
        Assert.Null(scheduler.GetLast(id));
    }

    [Fact]
    public async Task Shared_timer_stops_promptly_when_its_lifetime_is_canceled()
    {
        var clock = new FakeClock();
        await using var scheduler = new RecoveryScheduler(clock);
        using var cancellation = new CancellationTokenSource();
        Task running = scheduler.RunAsync(cancellation.Token);
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(scheduler.Pulse());
    }

    [Fact]
    public async Task Passive_discovery_reconciles_on_the_shared_timer_without_a_launch()
    {
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        Guid id = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.AddPassive(id, discovery, TimeSpan.FromSeconds(30), Detection.Absent());
        Assert.Empty(scheduler.Pulse());

        clock.Elapsed = TimeSpan.FromSeconds(30);
        discovery.Result = Detection.Present("session|target|1|started");
        await Assert.Single(scheduler.Pulse()).Value;
        Assert.Equal(DetectionKind.Present, scheduler.GetPassiveLast(id)?.Kind);
        clock.Elapsed = TimeSpan.FromSeconds(31);
        Assert.Empty(scheduler.Pulse());
        clock.Elapsed = TimeSpan.FromSeconds(60);
        discovery.Result = Detection.Unavailable("Controlled access denial");
        await Assert.Single(scheduler.Pulse()).Value;
        Assert.Equal(DetectionKind.Unavailable, scheduler.GetPassiveLast(id)?.Kind);
        await scheduler.RemoveAsync(id);
        Assert.Null(scheduler.GetPassiveLast(id));
    }

    [Fact]
    public async Task Policy_update_schedules_an_immediate_reconciliation()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        Guid id = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(id, ProfileCoordinator.CreateNew(id, RecoveryPolicy.Default,
            store, new ConstantDiscovery(Detection.Absent()),
            new CountingLauncher(), clock), RecoveryPolicy.Default);
        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(10);
        Assert.Empty(scheduler.Pulse());

        scheduler.UpdatePolicy(id, RecoveryPolicy.Default with
        {
            NormalPollInterval = TimeSpan.FromSeconds(60)
        });
        await Assert.Single(scheduler.Pulse()).Value;
    }

    [Fact]
    public async Task Interrupted_shared_launch_polls_at_observation_interval()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "34343434343434343434343434343434";
        Guid id = Guid.NewGuid();
        var originalClock = new FakeClock();
        var originalSession = new RecoverySessionStateStore(directory.Path, key, budgets);
        using (var original = ProfileCoordinator.CreateNew(id, AutoPolicy,
                   originalSession, new ConstantDiscovery(Detection.Absent()),
                   new CountingLauncher(), originalClock, sharedBudget: budgets))
        {
            originalClock.Elapsed = TimeSpan.Zero;
            await original.TickAsync();
            originalClock.Elapsed = TimeSpan.FromSeconds(2);
            await original.TickAsync();
            originalClock.Elapsed = TimeSpan.FromSeconds(7);
            Assert.True((await original.TickAsync()).LaunchDispatched);
        }

        var clock = new FakeClock();
        var reopened = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            new RecoverySessionStateStore(directory.Path, key, budgets),
            new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
            clock, sharedBudget: budgets);
        Assert.True(reopened.ReconciliationPending);
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(id, reopened, AutoPolicy);
        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(4);
        Assert.Empty(scheduler.Pulse());
        clock.Elapsed = TimeSpan.FromSeconds(5);
        await Assert.Single(scheduler.Pulse()).Value;
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class ConstantDiscovery(Detection detection) : IProcessDiscovery
    {
        public Task<Detection> DetectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(detection);
    }

    private sealed class BlockingDiscovery : IProcessDiscovery
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Detection.Absent();
        }
    }

    private sealed class MutableDiscovery(Detection initial) : IProcessDiscovery
    {
        public Detection Result { get; set; } = initial;
        public Task<Detection> DetectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private sealed class CountingLauncher : IProcessLauncher
    {
        public int Dispatches { get; private set; }
        public Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Dispatches++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-scheduler-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
