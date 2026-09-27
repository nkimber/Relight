using System.Text.Json;
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

    [Fact]
    public async Task Pause_and_resume_preserve_lockout_until_explicit_durable_reset()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new FailingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        RecoveryPolicy policy = AutoPolicy with { MaximumAutomaticAttempts = 1 };
        using var coordinator = ProfileCoordinator.CreateNew(id, policy, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock,
            recorder: recorder);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);
        Assert.True(coordinator.Snapshot.LockedOut);
        Assert.Equal(1, launcher.Dispatches);

        clock.Elapsed = TimeSpan.FromSeconds(40);
        await coordinator.SetPausedAsync(true);
        await TickAt(coordinator, clock, 100);
        await coordinator.SetPausedAsync(false);
        Assert.True(store.Load(id).Checkpoint.LockedOut);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.Equal(1, launcher.Dispatches);

        clock.Elapsed = TimeSpan.FromSeconds(102);
        await coordinator.ResetRecoveryAsync();
        Assert.False(store.Load(id).Checkpoint.LockedOut);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        await TickAt(coordinator, clock, 104);
        await TickAt(coordinator, clock, 133);
        Assert.Equal(1, launcher.Dispatches);
        await TickAt(coordinator, clock, 134);
        Assert.Equal(2, launcher.Dispatches);
        Assert.Contains(recorder.Events, entry => entry.Kind == OperationalEventKind.ProtectionPaused);
        Assert.Contains(recorder.Events, entry => entry.Kind == OperationalEventKind.ProtectionResumed);
        Assert.Contains(recorder.Events, entry => entry.Kind == OperationalEventKind.RecoveryReset);
    }

    [Fact]
    public async Task Reset_rejects_in_flight_launch_and_adopts_present_target()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            discovery, launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);
        Assert.Equal(RecoveryState.Starting, coordinator.Snapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ResetRecoveryAsync());
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);

        clock.Elapsed = TimeSpan.FromSeconds(33);
        discovery.Result = Detection.Present("session|app|100|start");
        await coordinator.TickAsync();
        clock.Elapsed = TimeSpan.FromSeconds(34);
        await coordinator.ResetRecoveryAsync();
        Assert.Equal(RecoveryState.Observing, coordinator.Snapshot.State);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.Equal(1, launcher.Dispatches);
    }

    [Fact]
    public async Task Failed_reset_write_keeps_durable_lockout_and_suspends_launches()
    {
        using var directory = new TestDirectory();
        var real = new RecoveryStateStore(directory.Path);
        var store = new SwitchableFailureStore(real);
        var clock = new FakeClock();
        var launcher = new FailingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            AutoPolicy with { MaximumAutomaticAttempts = 1 }, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);
        store.FailWrites = true;
        clock.Elapsed = TimeSpan.FromSeconds(40);
        await Assert.ThrowsAsync<RecoveryStateUnavailableException>(() =>
            coordinator.ResetRecoveryAsync());

        Assert.True(coordinator.StorageDegraded);
        Assert.True(real.Load(id).Checkpoint.LockedOut);
        Assert.Equal(1, real.Load(id).Checkpoint.ReservedAutomaticAttempts);
        await TickAt(coordinator, clock, 100);
        Assert.Equal(1, launcher.Dispatches);
    }

    [Fact]
    public async Task Explicit_start_adopts_existing_instance_without_dispatch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            RecoveryPolicy.Default, store,
            new ConstantDiscovery(Detection.Present("session|app|100|start")),
            launcher, clock);

        CoordinatorResult result = await coordinator.StartNowAsync();
        Assert.False(result.LaunchDispatched);
        Assert.Equal(RecoveryState.Observing, result.Snapshot.State);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Explicit_start_dispatches_without_charging_budget_and_observes_appearance()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            RecoveryPolicy.Default, store, discovery, launcher, clock,
            recorder: recorder);

        CoordinatorResult result = await coordinator.StartNowAsync();
        Assert.True(result.LaunchDispatched);
        Assert.Equal(1, launcher.Dispatches);
        RecoveryCheckpoint pending = store.Load(id).Checkpoint;
        Assert.Equal(0, pending.ReservedAutomaticAttempts);
        Assert.True(pending.PendingExplicitStart);
        Assert.True(pending.Armed);
        Assert.Equal(RecoveryState.Starting, pending.LastState);

        discovery.Result = Detection.Present("session|app|101|start");
        await TickAt(coordinator, clock, 1);
        Assert.Equal(ObservationOrigin.ExplicitStart,
            coordinator.Snapshot.ObservationOrigin);
        Assert.Null(store.Load(id).Checkpoint.PendingExplicitStart);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.ExplicitStartRequested);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.ExplicitStartDispatched);
    }

    [Fact]
    public async Task Explicit_start_during_zero_budget_lockout_keeps_lockout()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        RecoveryPolicy policy = AutoPolicy with { MaximumAutomaticAttempts = 0 };
        using var coordinator = ProfileCoordinator.CreateNew(id, policy, store,
            discovery, launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        Assert.True(coordinator.Snapshot.LockedOut);

        CoordinatorResult result = await coordinator.StartNowAsync();
        Assert.True(result.LaunchDispatched);
        Assert.Equal(1, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        discovery.Result = Detection.Present("session|app|101|start");
        await TickAt(coordinator, clock, 3);
        Assert.True(coordinator.Snapshot.LockedOut);
    }

    [Fact]
    public async Task Uncertain_explicit_dispatch_stays_pending_without_auto_duplicate()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new FailingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            RecoveryPolicy.Default, store, new ConstantDiscovery(Detection.Absent()),
            launcher, clock, recorder: recorder);

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartNowAsync());
        Assert.Equal(RecoveryState.Starting, coordinator.Snapshot.State);
        Assert.True(store.Load(id).Checkpoint.PendingExplicitStart);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.Equal(1, launcher.Dispatches);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.ExplicitStartUncertain);
        RecoveryMachine restored = RecoveryMachine.Restore(RecoveryPolicy.Default,
            store.Load(id).Checkpoint);
        Assert.True(restored.Snapshot.LockedOut);
        Assert.Equal(RecoveryHoldReason.InterruptedExplicitLaunch,
            restored.Snapshot.HoldReason);
        Assert.Equal(RecoverySignal.None,
            restored.Advance(Detection.Absent(), TimeSpan.Zero).Signal);
    }

    [Fact]
    public async Task Failed_explicit_reservation_write_prevents_dispatch()
    {
        using var directory = new TestDirectory();
        var real = new RecoveryStateStore(directory.Path);
        var store = new FailReservationStore(real);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            RecoveryPolicy.Default, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock);

        await Assert.ThrowsAsync<RecoveryStateUnavailableException>(() =>
            coordinator.StartNowAsync());
        Assert.True(coordinator.StorageDegraded);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Null(real.Load(id).Checkpoint.PendingExplicitStart);
    }

    [Fact]
    public async Task Explicit_appearance_timeout_retains_automatic_budget()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            RecoveryPolicy.Default, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock);

        await coordinator.StartNowAsync();
        await TickAt(coordinator, clock, 60);
        Assert.Equal(RecoveryState.RetryWaiting, coordinator.Snapshot.State);
        Assert.Equal(0, coordinator.Snapshot.ReservedAutomaticAttempts);
        Assert.Null(store.Load(id).Checkpoint.PendingExplicitStart);
        await TickAt(coordinator, clock, 90);
        Assert.Equal(2, launcher.Dispatches);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Reserved_attempt_and_dispatch_failure_emit_distinct_structured_events()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), new FailingLauncher(), clock,
            recorder: recorder);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);

        int reservation = recorder.Events.FindIndex(entry =>
            entry.Kind == OperationalEventKind.LaunchReserved);
        int failure = recorder.Events.FindIndex(entry =>
            entry.Kind == OperationalEventKind.LaunchFailed);
        Assert.True(reservation >= 0 && failure > reservation);
        Assert.Equal(id, recorder.Events[reservation].ProfileId);
        Assert.Equal(recorder.Events[reservation].OperationId,
            recorder.Events[failure].OperationId);
        Assert.DoesNotContain(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.LaunchDispatched);
        Assert.All(recorder.Events, entry => Assert.Equal(TimeSpan.Zero, entry.OccurredUtc.Offset));
    }

    [Fact]
    public async Task Full_event_queue_is_visible_but_does_not_prevent_a_reserved_launch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock,
            recorder: new RejectingRecorder());
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        CoordinatorResult result = await TickAt(coordinator, clock, 32);

        Assert.True(result.LaunchDispatched);
        Assert.True(result.LoggingDegraded);
        Assert.Equal(1, launcher.Dispatches);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Coordinator_events_reach_the_durable_journal_without_blocking_dispatch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        using var journal = new OperationalEventJournal(directory.Path, GlobalConfiguration.Default);
        var clock = new FakeClock();
        Guid id = Guid.NewGuid();
        await using (var recorder = new QueuedEventRecorder(journal))
        {
            using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
                new ConstantDiscovery(Detection.Absent()), new FailingLauncher(), clock,
                recorder: recorder);
            await TickAt(coordinator, clock, 0);
            await TickAt(coordinator, clock, 2);
            await TickAt(coordinator, clock, 32);
        }

        string logs = Path.Combine(directory.Path, "Logs");
        string[] lines = Directory.GetFiles(logs, "*.jsonl")
            .SelectMany(File.ReadAllLines).ToArray();
        using var documents = new DisposableDocuments(lines.Select(line => JsonDocument.Parse(line)));
        string[] kinds = documents.Values.Select(document =>
            document.RootElement.GetProperty("kind").GetString()!).ToArray();
        Assert.Contains("LaunchReserved", kinds);
        Assert.Contains("LaunchFailed", kinds);
        Assert.DoesNotContain("LaunchDispatched", kinds);
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

    private sealed class CapturingRecorder : IEventRecorder
    {
        public List<OperationalEvent> Events { get; } = [];
        public bool TryRecord(OperationalEvent entry)
        {
            Events.Add(entry);
            return true;
        }
    }

    private sealed class RejectingRecorder : IEventRecorder
    {
        public bool TryRecord(OperationalEvent entry) => false;
    }

    private sealed class DisposableDocuments(IEnumerable<JsonDocument> documents) : IDisposable
    {
        public JsonDocument[] Values { get; } = documents.ToArray();
        public void Dispose()
        {
            foreach (JsonDocument document in Values) document.Dispose();
        }
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

    private sealed class SwitchableFailureStore(IRecoveryStateStore inner) : IRecoveryStateStore
    {
        public bool FailWrites { get; set; }
        public StoredRecoveryState Create(Guid id, RecoveryCheckpoint state) => inner.Create(id, state);
        public StoredRecoveryState Load(Guid id) => inner.Load(id);
        public StoredRecoveryState Save(Guid id, long revision, RecoveryCheckpoint state) =>
            FailWrites ? throw new RecoveryStateUnavailableException("Controlled disk failure.") :
                inner.Save(id, revision, state);
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
