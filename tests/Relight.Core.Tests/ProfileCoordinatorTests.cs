using Relight.Core;
using Relight.Engine;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class ProfileCoordinatorTests
{
    private static RecoveryPolicy AutoPolicy => RecoveryPolicy.Default with
    {
        StartAutomaticallyWhenInitiallyAbsent = true
    };

    [Fact]
    public async Task Three_failed_launches_are_reserved_before_dispatch_and_never_repeated()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new FailingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), launcher, clock);

        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        for (int i = 1; i <= 3; i++)
        {
            CoordinatorResult result = await TickAt(coordinator, clock, 2 + 30 * i);
            Assert.Equal(i, launcher.Dispatches);
            Assert.False(result.StorageDegraded);
            Assert.Equal(i, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        }
        await TickAt(coordinator, clock, 300);
        Assert.Equal(3, launcher.Dispatches);
        Assert.True(store.Load(id).Checkpoint.LockedOut);
        Assert.Equal(RecoveryState.AwaitingIntervention, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task Failed_reservation_write_prevents_any_launch()
    {
        using var directory = new TestDirectory();
        var real = new RecoveryStateStore(directory.Path);
        var store = new FailReservationStore(real);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        CoordinatorResult result = await TickAt(coordinator, clock, 32);

        Assert.True(result.StorageDegraded);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, real.Load(id).Checkpoint.ReservedAutomaticAttempts);
        await TickAt(coordinator, clock, 200);
        Assert.Equal(0, launcher.Dispatches);
    }

    [Fact]
    public async Task Second_discovery_adopts_external_start_before_dispatch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new SequencedDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, discovery, launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        discovery.Queue(Detection.Absent(), Detection.Present("external-instance"));
        CoordinatorResult result = await TickAt(coordinator, clock, 32);

        Assert.False(result.LaunchDispatched);
        Assert.Equal(RecoveryState.Observing, result.Snapshot.State);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Concurrent_ticks_share_one_launch_operation_per_profile()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new BlockingLauncher();
        using var coordinator = ProfileCoordinator.CreateNew(Guid.NewGuid(), AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        clock.Elapsed = TimeSpan.FromSeconds(32);
        Task<CoordinatorResult> first = coordinator.TickAsync();
        await launcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<CoordinatorResult> second = coordinator.TickAsync();
        Assert.Equal(1, launcher.Dispatches);
        launcher.Release.SetResult();
        CoordinatorResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(results, result => result.LaunchDispatched);
        Assert.Equal(1, launcher.Dispatches);
    }

    [Fact]
    public async Task One_blocked_profile_does_not_stall_another()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        using var queue = new BoundedLaunchGate(2);
        var firstClock = new FakeClock();
        var secondClock = new FakeClock();
        var blocked = new BlockingLauncher();
        var free = new CountingLauncher();
        using var a = ProfileCoordinator.CreateNew(Guid.NewGuid(), AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), blocked, firstClock, queue);
        using var b = ProfileCoordinator.CreateNew(Guid.NewGuid(), AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), free, secondClock, queue);
        foreach (int second in new[] { 0, 2 })
        {
            await TickAt(a, firstClock, second);
            await TickAt(b, secondClock, second);
        }
        firstClock.Elapsed = TimeSpan.FromSeconds(32);
        Task<CoordinatorResult> waiting = a.TickAsync();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        CoordinatorResult independent = await TickAt(b, secondClock, 32);
        Assert.True(independent.LaunchDispatched);
        Assert.Equal(1, free.Dispatches);
        blocked.Release.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Pause_cancels_a_blocked_launch_without_refunding_its_reservation()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var blocked = new BlockingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), blocked, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        clock.Elapsed = TimeSpan.FromSeconds(32);
        Task<CoordinatorResult> launch = coordinator.TickAsync();
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task pause = coordinator.SetPausedAsync(true);
        await Task.WhenAll(launch, pause).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, blocked.Dispatches);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.True(coordinator.Snapshot.Paused);
        await TickAt(coordinator, clock, 100);
        Assert.Equal(1, blocked.Dispatches);
    }

    [Fact]
    public async Task Queued_profile_adopts_target_that_appears_before_its_launch_slot()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        using var queue = new BoundedLaunchGate(2);
        var firstClock = new FakeClock();
        var secondClock = new FakeClock();
        var queuedClock = new FakeClock();
        var firstLaunch = new BlockingLauncher();
        var secondLaunch = new BlockingLauncher();
        var queuedLaunch = new CountingLauncher();
        var queuedDiscovery = new MutableDiscovery(Detection.Absent());
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid(), queuedId = Guid.NewGuid();
        using var first = ProfileCoordinator.CreateNew(firstId, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), firstLaunch, firstClock, queue);
        using var second = ProfileCoordinator.CreateNew(secondId, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), secondLaunch, secondClock, queue);
        using var queued = ProfileCoordinator.CreateNew(queuedId, AutoPolicy, store,
            queuedDiscovery, queuedLaunch, queuedClock, queue);
        foreach (int secondMark in new[] { 0, 2 })
        {
            await TickAt(first, firstClock, secondMark);
            await TickAt(second, secondClock, secondMark);
            await TickAt(queued, queuedClock, secondMark);
        }

        firstClock.Elapsed = secondClock.Elapsed = queuedClock.Elapsed = TimeSpan.FromSeconds(32);
        Task<CoordinatorResult> runningFirst = first.TickAsync();
        Task<CoordinatorResult> runningSecond = second.TickAsync();
        await firstLaunch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await secondLaunch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<CoordinatorResult> waiting = queued.TickAsync();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, store.Load(queuedId).Checkpoint.ReservedAutomaticAttempts);

        queuedDiscovery.Result = Detection.Present("current-session|target|100|start");
        firstLaunch.Release.TrySetResult();
        await runningFirst.WaitAsync(TimeSpan.FromSeconds(3));
        CoordinatorResult adopted = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(RecoveryState.Observing, adopted.Snapshot.State);
        Assert.Equal(0, queuedLaunch.Dispatches);
        Assert.Equal(0, store.Load(queuedId).Checkpoint.ReservedAutomaticAttempts);
        secondLaunch.Release.TrySetResult();
        await runningSecond.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Pause_while_waiting_for_launch_slot_does_not_consume_an_attempt()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var queue = new BlockingLaunchGate();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock, queue);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        clock.Elapsed = TimeSpan.FromSeconds(32);
        Task<CoordinatorResult> waiting = coordinator.TickAsync();
        await queue.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task pause = coordinator.SetPausedAsync(true);
        await Task.WhenAll(waiting, pause).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.True(coordinator.Snapshot.Paused);
    }

    private static async Task<CoordinatorResult> TickAt(ProfileCoordinator coordinator,
        FakeClock clock, int seconds)
    {
        clock.Elapsed = TimeSpan.FromSeconds(seconds);
        return await coordinator.TickAsync();
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class ConstantDiscovery(Detection result) : IProcessDiscovery
    {
        public Task<Detection> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class MutableDiscovery(Detection result) : IProcessDiscovery
    {
        public Detection Result { get; set; } = result;
        public Task<Detection> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    private sealed class SequencedDiscovery(Detection fallback) : IProcessDiscovery
    {
        private readonly Queue<Detection> _next = new();
        public void Queue(params Detection[] values)
        {
            foreach (Detection value in values) _next.Enqueue(value);
        }
        public Task<Detection> DetectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_next.Count > 0 ? _next.Dequeue() : fallback);
    }

    private class CountingLauncher : IProcessLauncher
    {
        public int Dispatches { get; private set; }
        public virtual Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Dispatches++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingLauncher : CountingLauncher
    {
        public override async Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
        {
            await base.LaunchAsync(operationId, cancellationToken);
            throw new IOException("Controlled launch failure.");
        }
    }

    private sealed class BlockingLauncher : IProcessLauncher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Dispatches { get; private set; }
        public async Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
        {
            Dispatches++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockingLaunchGate : ILaunchGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new NoopLease();
        }
        private sealed class NoopLease : IDisposable { public void Dispose() { } }
    }

    private sealed class FailReservationStore(IRecoveryStateStore inner) : IRecoveryStateStore
    {
        public StoredRecoveryState Create(Guid id, RecoveryCheckpoint state) => inner.Create(id, state);
        public StoredRecoveryState Load(Guid id) => inner.Load(id);
        public StoredRecoveryState Save(Guid id, long revision, RecoveryCheckpoint state) =>
            state.LastState == RecoveryState.Starting
                ? throw new RecoveryStateUnavailableException("Controlled disk failure.")
                : inner.Save(id, revision, state);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-engine-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
