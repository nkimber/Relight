using Relight.Core;
using Relight.Engine;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class RecoverySchedulerTests
{
    [Fact]
    public async Task Missed_heartbeat_reconciles_before_any_overdue_profile_poll()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, discovery, launcher, clock, recorder: recorder);
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(id, coordinator, AutoPolicy);
        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(2);
        await Assert.Single(scheduler.Pulse()).Value;
        Assert.Equal(0, launcher.Dispatches);

        clock.Elapsed = TimeSpan.FromHours(1);
        discovery.Result = Detection.Present("verified-after-wake");
        Assert.Empty(scheduler.PulseAfterMonitoringGap(TimeSpan.FromSeconds(2)));
        await Assert.IsAssignableFrom<Task>(scheduler.GetGapReconciliation(id))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.Single(scheduler.Pulse()).Value;

        Assert.Equal("verified-after-wake", coordinator.Snapshot.TargetIdentity);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.MonitoringGap);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.MonitoringRestored);
    }

    [Fact]
    public async Task One_blocked_profile_does_not_hold_other_gap_reconciliation()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var blocked = new BlockingDiscovery();
        Guid blockedId = Guid.NewGuid(), otherId = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(blockedId, ProfileCoordinator.CreateNew(blockedId, AutoPolicy,
            store, blocked, new CountingLauncher(), clock), AutoPolicy);
        scheduler.Add(otherId, ProfileCoordinator.CreateNew(otherId, AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()),
            new CountingLauncher(), clock), AutoPolicy);
        IReadOnlyDictionary<Guid, Task> initial = scheduler.Pulse();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await initial[otherId].WaitAsync(TimeSpan.FromSeconds(5));

        clock.Elapsed = TimeSpan.FromHours(1);
        IReadOnlyDictionary<Guid, Task> started = scheduler.PulseAfterMonitoringGap(
            TimeSpan.Zero);
        await Assert.IsAssignableFrom<Task>(scheduler.GetGapReconciliation(otherId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Assert.IsAssignableFrom<Task>(
            scheduler.GetGapReconciliation(blockedId)).IsCompleted);
        Assert.False(initial[blockedId].IsCompleted);
        Task otherPoll = started.TryGetValue(otherId, out Task? alreadyStarted)
            ? alreadyStarted : scheduler.Pulse()[otherId];
        await otherPoll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(scheduler.GetLast(otherId)?.Result);

        blocked.Release.TrySetResult();
        await initial[blockedId].WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.IsAssignableFrom<Task>(scheduler.GetGapReconciliation(blockedId))
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

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
    public async Task Fifty_profiles_keep_independent_deadlines_and_budgets_when_one_blocks()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var blocked = new BlockingDiscovery();
        Guid blockedId = Guid.NewGuid();
        var launchers = new Dictionary<Guid, CountingLauncher>();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(blockedId, ProfileCoordinator.CreateNew(blockedId, AutoPolicy,
            store, blocked, new CountingLauncher(), clock), AutoPolicy);
        for (int index = 0; index < 49; index++)
        {
            Guid id = Guid.NewGuid();
            var launcher = new CountingLauncher();
            launchers.Add(id, launcher);
            scheduler.Add(id, ProfileCoordinator.CreateNew(id, AutoPolicy, store,
                new ConstantDiscovery(Detection.Absent()), launcher, clock), AutoPolicy);
        }

        IReadOnlyDictionary<Guid, Task> first = scheduler.Pulse();
        Assert.Equal(50, first.Count);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await Task.WhenAll(first.Where(pair => pair.Key != blockedId)
                .Select(pair => pair.Value)).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(first[blockedId].IsCompleted);
            Assert.All(launchers, pair => Assert.Equal(0, pair.Value.Dispatches));

            clock.Elapsed = TimeSpan.FromSeconds(2);
            IReadOnlyDictionary<Guid, Task> confirmed = scheduler.Pulse();
            Assert.Equal(49, confirmed.Count);
            await Task.WhenAll(confirmed.Values).WaitAsync(TimeSpan.FromSeconds(15));
            clock.Elapsed = TimeSpan.FromSeconds(7);
            IReadOnlyDictionary<Guid, Task> dispatched = scheduler.Pulse();
            Assert.Equal(49, dispatched.Count);
            await Task.WhenAll(dispatched.Values).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.All(launchers, pair =>
            {
                Assert.Equal(1, pair.Value.Dispatches);
                Assert.Equal(1, store.Load(pair.Key).Checkpoint.ReservedAutomaticAttempts);
            });
            Assert.Equal(0, store.Load(blockedId).Checkpoint.ReservedAutomaticAttempts);
        }
        finally
        {
            blocked.Release.TrySetResult();
            await first[blockedId].WaitAsync(TimeSpan.FromSeconds(15));
        }
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
    public async Task Exit_signal_and_manual_start_during_due_poll_dispatch_only_one_operation()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new ThirdCallBlockingDiscovery();
        var launcher = new CountingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            discovery, launcher, clock, recorder: recorder);
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.Add(id, coordinator, AutoPolicy);

        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(2);
        await Assert.Single(scheduler.Pulse()).Value;
        clock.Elapsed = TimeSpan.FromSeconds(7);
        Task duePoll = Assert.Single(scheduler.Pulse()).Value;
        await discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        scheduler.RequestImmediate(id); // A process-exit callback requests another poll.
        Assert.Empty(scheduler.Pulse()); // The first poll still owns this profile.
        Task<CoordinatorResult> manualStart = coordinator.StartNowAsync();
        discovery.Release.TrySetResult();
        await duePoll.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manualStart);
        await Assert.Single(scheduler.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, launcher.Dispatches);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        OperationalEvent reserved = Assert.Single(recorder.Events,
            entry => entry.Kind == OperationalEventKind.LaunchReserved);
        OperationalEvent dispatched = Assert.Single(recorder.Events,
            entry => entry.Kind == OperationalEventKind.LaunchDispatched);
        Assert.NotNull(reserved.OperationId);
        Assert.Equal(reserved.OperationId, dispatched.OperationId);
        Assert.DoesNotContain(recorder.Events,
            entry => entry.Kind == OperationalEventKind.ExplicitStartRequested);
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
    public async Task Windows_interruption_wakes_passive_profiles_before_their_next_poll()
    {
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        Guid id = Guid.NewGuid();
        await using var scheduler = new RecoveryScheduler(clock);
        scheduler.AddPassive(id, discovery, TimeSpan.FromSeconds(30), Detection.Absent());
        clock.Elapsed = TimeSpan.FromSeconds(1);
        discovery.Result = Detection.Present("session|target|1|started");

        Assert.Empty(scheduler.Pulse());
        scheduler.RequestImmediatePassive();
        await Assert.Single(scheduler.Pulse()).Value;

        Assert.Equal(DetectionKind.Present, scheduler.GetPassiveLast(id)?.Kind);
        Assert.Empty(scheduler.Pulse());
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

    private sealed class ThirdCallBlockingDiscovery : IProcessDiscovery
    {
        private int _calls;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 3)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return Detection.Absent();
        }
    }

    private sealed class CapturingRecorder : IEventRecorder
    {
        public List<OperationalEvent> Events { get; } = [];
        public bool TryRecord(OperationalEvent entry)
        {
            Events.Add(entry);
            return true;
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
