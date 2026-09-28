using System.Text.Json;
using System.ComponentModel;
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
    public async Task Shared_budget_is_the_authority_for_three_failed_coordinator_launches()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", budgets);
        var clock = new FakeClock();
        var launcher = new FailingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            session, new ConstantDiscovery(Detection.Absent()), launcher, clock,
            sharedBudget: budgets);

        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            CoordinatorResult result = await TickAt(coordinator, clock,
                2 + 30 * attempt);
            Assert.False(result.StorageDegraded, result.Error);
            Assert.Equal(attempt, launcher.Dispatches);
            SharedRecoveryBudget committed = budgets.Load(id);
            Assert.Equal(attempt, committed.ReservedAutomaticAttempts);
            Assert.Null(committed.PendingAutomaticOperationId);
        }
        await TickAt(coordinator, clock, 300);
        Assert.Equal(3, launcher.Dispatches);
        Assert.True(budgets.Load(id).LockedOut);
        Assert.Equal(RecoveryState.AwaitingIntervention, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task Shared_budget_stable_observation_releases_charged_episode()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", budgets);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        RecoveryPolicy policy = AutoPolicy with
        {
            ObservationPeriod = TimeSpan.FromSeconds(60),
            ObservationPollInterval = TimeSpan.FromSeconds(30)
        };
        using var coordinator = ProfileCoordinator.CreateNew(id, policy,
            session, discovery, launcher, clock, sharedBudget: budgets);

        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        CoordinatorResult launched = await TickAt(coordinator, clock, 32);
        Assert.True(launched.LaunchDispatched);
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);

        discovery.Result = Detection.Present("current-session-target");
        CoordinatorResult observed = await TickAt(coordinator, clock, 33);
        Assert.False(observed.StorageDegraded, observed.Error);
        Assert.Null(budgets.Load(id).PendingAutomaticOperationId);
        await TickAt(coordinator, clock, 63);
        CoordinatorResult healthy = await TickAt(coordinator, clock, 93);
        Assert.False(healthy.StorageDegraded, healthy.Error);
        Assert.Equal(RecoveryState.Healthy, healthy.Snapshot.State);
        Assert.Equal(0, budgets.Load(id).ReservedAutomaticAttempts);
        Assert.Null(budgets.Load(id).EpisodeId);
    }

    [Fact]
    public async Task Another_sessions_budget_change_suspends_dispatch()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", budgets);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            session, new ConstantDiscovery(Detection.Absent()), launcher, clock,
            sharedBudget: budgets);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        SharedRecoveryBudget prior = budgets.Load(id);
        var otherSignIn = new SharedRecoveryBudgetStore(directory.Path);
        otherSignIn.ResetExplicitly(id, prior.Revision);

        CoordinatorResult due = await TickAt(coordinator, clock, 32);
        Assert.True(due.StorageDegraded);
        Assert.False(due.LaunchDispatched);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, budgets.Load(id).ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Shared_explicit_start_has_a_distinct_pending_marker_without_attempt_charge()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", budgets);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, RecoveryPolicy.Default,
            session, discovery, launcher, clock, sharedBudget: budgets);

        CoordinatorResult started = await coordinator.StartNowAsync();
        Assert.True(started.LaunchDispatched);
        Assert.Equal(1, launcher.Dispatches);
        SharedRecoveryBudget pending = budgets.Load(id);
        Assert.Equal(0, pending.ReservedAutomaticAttempts);
        Assert.Null(pending.PendingAutomaticOperationId);
        Assert.NotNull(pending.PendingExplicitOperationId);

        discovery.Result = Detection.Present("current-session-target");
        CoordinatorResult observed = await TickAt(coordinator, clock, 1);
        Assert.False(observed.StorageDegraded, observed.Error);
        Assert.Equal(RecoveryState.Observing, observed.Snapshot.State);
        Assert.Null(budgets.Load(id).PendingExplicitOperationId);
        Assert.Equal(0, budgets.Load(id).ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Restart_waits_full_appearance_timeout_before_resolving_charged_attempt()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", budgets);
        var clock = new FakeClock();
        Guid id = Guid.NewGuid();
        var firstLauncher = new CountingLauncher();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy, session,
                   new ConstantDiscovery(Detection.Absent()), firstLauncher, clock,
                   sharedBudget: budgets))
        {
            await TickAt(first, clock, 0);
            await TickAt(first, clock, 2);
            CoordinatorResult launched = await TickAt(first, clock, 32);
            Assert.True(launched.LaunchDispatched);
        }
        SharedRecoveryBudget pending = budgets.Load(id);
        Assert.Equal(1, pending.ReservedAutomaticAttempts);
        Assert.NotNull(pending.PendingAutomaticOperationId);

        var restartedClock = new FakeClock();
        var secondLauncher = new CountingLauncher();
        using var restarted = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            new RecoverySessionStateStore(directory.Path,
                "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", budgets),
            new ConstantDiscovery(Detection.Absent()), secondLauncher,
            restartedClock, sharedBudget: budgets);
        await TickAt(restarted, restartedClock, 0);
        for (int second = 5; second <= 55; second += 5)
            await TickAt(restarted, restartedClock, second);
        await TickAt(restarted, restartedClock, 59);
        Assert.Equal(pending, budgets.Load(id));
        CoordinatorResult reconciled = await TickAt(restarted, restartedClock, 60);
        Assert.False(reconciled.StorageDegraded, reconciled.Error);
        Assert.False(reconciled.LaunchDispatched);
        Assert.Equal(0, secondLauncher.Dispatches);
        SharedRecoveryBudget charged = budgets.Load(id);
        Assert.Equal(1, charged.ReservedAutomaticAttempts);
        Assert.Null(charged.PendingAutomaticOperationId);

        await TickAt(restarted, restartedClock, 62);
        CoordinatorResult next = await TickAt(restarted, restartedClock, 92);
        Assert.True(next.LaunchDispatched);
        Assert.Equal(1, secondLauncher.Dispatches);
        Assert.Equal(2, budgets.Load(id).ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Restart_adopts_present_target_without_refunding_interrupted_attempt()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "77777777777777777777777777777777";
        var session = new RecoverySessionStateStore(directory.Path, key, budgets);
        var clock = new FakeClock();
        Guid id = Guid.NewGuid();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy, session,
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   clock, sharedBudget: budgets))
        {
            await TickAt(first, clock, 0);
            await TickAt(first, clock, 2);
            Assert.True((await TickAt(first, clock, 32)).LaunchDispatched);
        }
        var restartedClock = new FakeClock();
        var launcher = new CountingLauncher();
        using var restarted = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            new RecoverySessionStateStore(directory.Path, key, budgets),
            new ConstantDiscovery(Detection.Present("current-session-target")),
            launcher, restartedClock, sharedBudget: budgets);

        CoordinatorResult observed = await TickAt(restarted, restartedClock, 0);

        Assert.False(observed.StorageDegraded, observed.Error);
        Assert.False(observed.LaunchDispatched);
        Assert.Equal(RecoveryState.Observing, observed.Snapshot.State);
        Assert.Equal(0, launcher.Dispatches);
        SharedRecoveryBudget charged = budgets.Load(id);
        Assert.Equal(1, charged.ReservedAutomaticAttempts);
        Assert.Null(charged.PendingAutomaticOperationId);
    }

    [Fact]
    public async Task New_sign_in_never_resolves_another_sessions_pending_launch()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string firstKey = "66666666666666666666666666666666";
        var firstSession = new RecoverySessionStateStore(directory.Path, firstKey, budgets);
        var clock = new FakeClock();
        Guid id = Guid.NewGuid();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy, firstSession,
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   clock, sharedBudget: budgets))
        {
            await TickAt(first, clock, 0);
            await TickAt(first, clock, 2);
            Assert.True((await TickAt(first, clock, 32)).LaunchDispatched);
        }
        SharedRecoveryBudget pending = budgets.Load(id);
        const string secondKey = "55555555555555555555555555555555";
        var secondSession = new RecoverySessionStateStore(directory.Path, secondKey, budgets);
        secondSession.InitializeForNewSignIn(id, true);
        var secondClock = new FakeClock();
        var secondLauncher = new CountingLauncher();
        using var second = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            secondSession, new ConstantDiscovery(Detection.Absent()),
            secondLauncher, secondClock, sharedBudget: budgets);

        CoordinatorResult result = await TickAt(second, secondClock, 120);

        Assert.True(result.StorageDegraded);
        Assert.Equal(0, secondLauncher.Dispatches);
        Assert.Equal(pending, budgets.Load(id));
    }

    [Fact]
    public async Task Pause_and_second_restart_preserve_interrupted_operation_ownership()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "44444444444444444444444444444444";
        Guid id = Guid.NewGuid();
        var firstClock = new FakeClock();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy,
                   new RecoverySessionStateStore(directory.Path, key, budgets),
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   firstClock, sharedBudget: budgets))
        {
            await TickAt(first, firstClock, 0);
            await TickAt(first, firstClock, 2);
            Assert.True((await TickAt(first, firstClock, 32)).LaunchDispatched);
        }
        using (var paused = ProfileCoordinator.OpenExisting(id, AutoPolicy,
                   new RecoverySessionStateStore(directory.Path, key, budgets),
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   new FakeClock(), sharedBudget: budgets))
            await paused.SetPausedAsync(true);

        var resumedClock = new FakeClock();
        var launcher = new CountingLauncher();
        using var resumed = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            new RecoverySessionStateStore(directory.Path, key, budgets),
            new ConstantDiscovery(Detection.Absent()), launcher, resumedClock,
            sharedBudget: budgets);
        Assert.False(resumed.StorageDegraded, resumed.StorageError);
        await TickAt(resumed, resumedClock, 120);
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);
        await resumed.SetPausedAsync(false);
        await TickAt(resumed, resumedClock, 120);
        for (int second = 125; second <= 175; second += 5)
            await TickAt(resumed, resumedClock, second);
        await TickAt(resumed, resumedClock, 179);
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);
        await TickAt(resumed, resumedClock, 180);
        Assert.Null(budgets.Load(id).PendingAutomaticOperationId);
        Assert.Equal(1, budgets.Load(id).ReservedAutomaticAttempts);
        Assert.Equal(0, launcher.Dispatches);
    }

    [Fact]
    public async Task Disabling_after_dispatch_preserves_pending_operation_across_restart()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "33333333333333333333333333333333";
        Guid id = Guid.NewGuid();
        var clock = new FakeClock();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy,
                   new RecoverySessionStateStore(directory.Path, key, budgets),
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   clock, sharedBudget: budgets))
        {
            await TickAt(first, clock, 0);
            await TickAt(first, clock, 2);
            Assert.True((await TickAt(first, clock, 32)).LaunchDispatched);
            await first.SetEnabledAsync(false);
        }
        var session = new RecoverySessionStateStore(directory.Path, key, budgets);
        RecoveryCheckpoint saved = session.Load(id).Checkpoint;
        Assert.False(saved.Enabled);
        Assert.Equal(RecoveryState.Starting, saved.LastState);
        Assert.NotNull(saved.PendingOperationId);

        var reopenedClock = new FakeClock();
        var launcher = new CountingLauncher();
        using var reopened = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            session, new ConstantDiscovery(Detection.Absent()), launcher,
            reopenedClock, sharedBudget: budgets);
        Assert.False(reopened.StorageDegraded, reopened.StorageError);
        await TickAt(reopened, reopenedClock, 100);
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);
        await reopened.SetEnabledAsync(true);
        await TickAt(reopened, reopenedClock, 100);
        for (int second = 105; second <= 155; second += 5)
            await TickAt(reopened, reopenedClock, second);
        await TickAt(reopened, reopenedClock, 160);
        Assert.Null(budgets.Load(id).PendingAutomaticOperationId);
        Assert.Equal(1, budgets.Load(id).ReservedAutomaticAttempts);
        Assert.Equal(0, launcher.Dispatches);
    }

    [Fact]
    public async Task Unavailable_detection_restarts_interrupted_appearance_wait()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "22222222222222222222222222222222";
        Guid id = Guid.NewGuid();
        var firstClock = new FakeClock();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy,
                   new RecoverySessionStateStore(directory.Path, key, budgets),
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   firstClock, sharedBudget: budgets))
        {
            await TickAt(first, firstClock, 0);
            await TickAt(first, firstClock, 2);
            Assert.True((await TickAt(first, firstClock, 32)).LaunchDispatched);
        }

        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        using var restarted = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            new RecoverySessionStateStore(directory.Path, key, budgets),
            discovery, launcher, clock, sharedBudget: budgets);
        await TickAt(restarted, clock, 0);
        discovery.Result = Detection.Unavailable("Inspection interrupted.");
        await TickAt(restarted, clock, 30);
        discovery.Result = Detection.Absent();
        await TickAt(restarted, clock, 59);
        for (int second = 64; second <= 114; second += 5)
            await TickAt(restarted, clock, second);
        await TickAt(restarted, clock, 118);
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);
        CoordinatorResult reconciled = await TickAt(restarted, clock, 119);
        Assert.False(reconciled.StorageDegraded, reconciled.Error);
        Assert.Null(budgets.Load(id).PendingAutomaticOperationId);
        Assert.Equal(1, budgets.Load(id).ReservedAutomaticAttempts);
        Assert.Equal(0, launcher.Dispatches);
    }

    [Fact]
    public async Task Missed_interrupted_poll_does_not_count_as_continuous_absence()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "12121212121212121212121212121212";
        Guid id = Guid.NewGuid();
        var firstClock = new FakeClock();
        using (var first = ProfileCoordinator.CreateNew(id, AutoPolicy,
                   new RecoverySessionStateStore(directory.Path, key, budgets),
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   firstClock, sharedBudget: budgets))
        {
            await TickAt(first, firstClock, 0);
            await TickAt(first, firstClock, 2);
            Assert.True((await TickAt(first, firstClock, 32)).LaunchDispatched);
        }
        var clock = new FakeClock();
        using var restarted = ProfileCoordinator.OpenExisting(id, AutoPolicy,
            new RecoverySessionStateStore(directory.Path, key, budgets),
            new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
            clock, sharedBudget: budgets);

        await TickAt(restarted, clock, 0);
        await TickAt(restarted, clock, 60);
        Assert.NotNull(budgets.Load(id).PendingAutomaticOperationId);
        for (int second = 65; second <= 120; second += 5)
            await TickAt(restarted, clock, second);
        Assert.Null(budgets.Load(id).PendingAutomaticOperationId);
        Assert.Equal(1, budgets.Load(id).ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Interrupted_explicit_start_can_be_reset_after_full_reconciliation()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        const string key = "11111111111111111111111111111111";
        Guid id = Guid.NewGuid();
        using (var first = ProfileCoordinator.CreateNew(id,
                   RecoveryPolicy.Default,
                   new RecoverySessionStateStore(directory.Path, key, budgets),
                   new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
                   new FakeClock(), sharedBudget: budgets))
            Assert.True((await first.StartNowAsync()).LaunchDispatched);
        Assert.NotNull(budgets.Load(id).PendingExplicitOperationId);

        var clock = new FakeClock();
        using var restarted = ProfileCoordinator.OpenExisting(id,
            RecoveryPolicy.Default,
            new RecoverySessionStateStore(directory.Path, key, budgets),
            new ConstantDiscovery(Detection.Absent()), new CountingLauncher(),
            clock, sharedBudget: budgets);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.ResetRecoveryAsync());
        await TickAt(restarted, clock, 0);
        for (int second = 5; second <= 55; second += 5)
            await TickAt(restarted, clock, second);
        await TickAt(restarted, clock, 60);
        Assert.Null(budgets.Load(id).PendingExplicitOperationId);
        Assert.Equal(0, budgets.Load(id).ReservedAutomaticAttempts);
        await restarted.ResetRecoveryAsync();
        Assert.False(restarted.StorageDegraded, restarted.StorageError);
        Assert.False(restarted.Snapshot.LockedOut);
    }

    [Fact]
    public async Task Target_appearing_after_shared_reservation_averts_dispatch_without_refund()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", budgets);
        var clock = new FakeClock();
        var discovery = new SequencedDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            session, discovery, launcher, clock, recorder: recorder,
            sharedBudget: budgets);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        discovery.Queue(Detection.Absent(), Detection.Absent(),
            Detection.Present("external-after-reservation"));

        CoordinatorResult result = await TickAt(coordinator, clock, 32);

        Assert.False(result.LaunchDispatched);
        Assert.False(result.StorageDegraded, result.Error);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(RecoveryState.Observing, result.Snapshot.State);
        SharedRecoveryBudget charged = budgets.Load(id);
        Assert.Equal(1, charged.ReservedAutomaticAttempts);
        Assert.Null(charged.PendingAutomaticOperationId);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.LaunchAverted);
    }

    [Fact]
    public async Task Unknown_final_discovery_keeps_shared_reservation_and_never_dispatches()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "88888888888888888888888888888888", budgets);
        var clock = new FakeClock();
        var discovery = new SequencedDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            session, discovery, launcher, clock, sharedBudget: budgets);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        discovery.Queue(Detection.Absent(), Detection.Absent(),
            Detection.Unavailable("Candidate identity changed."));

        CoordinatorResult result = await TickAt(coordinator, clock, 32);

        Assert.False(result.LaunchDispatched);
        Assert.Equal(0, launcher.Dispatches);
        Assert.True(result.Snapshot.DetectionUnavailable);
        SharedRecoveryBudget charged = budgets.Load(id);
        Assert.Equal(1, charged.ReservedAutomaticAttempts);
        Assert.NotNull(charged.PendingAutomaticOperationId);
    }

    [Fact]
    public async Task Target_appearing_after_explicit_marker_averts_manual_dispatch()
    {
        using var directory = new TestDirectory();
        var budgets = new SharedRecoveryBudgetStore(directory.Path);
        var session = new RecoverySessionStateStore(directory.Path,
            "99999999999999999999999999999999", budgets);
        var discovery = new SequencedDiscovery(Detection.Absent());
        var launcher = new CountingLauncher();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            RecoveryPolicy.Default, session, discovery, launcher, new FakeClock(),
            recorder: recorder, sharedBudget: budgets);
        discovery.Queue(Detection.Absent(),
            Detection.Present("external-after-explicit-marker"));

        CoordinatorResult result = await coordinator.StartNowAsync();

        Assert.False(result.LaunchDispatched);
        Assert.False(result.StorageDegraded, result.Error);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(RecoveryState.Observing, result.Snapshot.State);
        SharedRecoveryBudget budget = budgets.Load(id);
        Assert.Equal(0, budget.ReservedAutomaticAttempts);
        Assert.Null(budget.PendingExplicitOperationId);
        Assert.Contains(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.ExplicitStartAverted);
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
    public async Task Late_tick_reconfirms_absence_before_reserving_automatic_launch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);

        CoordinatorResult resumed = await TickAt(coordinator, clock, 300);
        Assert.False(resumed.LaunchDispatched);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);

        await TickAt(coordinator, clock, 302);
        CoordinatorResult due = await TickAt(coordinator, clock, 332);
        Assert.True(due.LaunchDispatched);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
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
    public async Task Identity_change_cancels_queued_launch_and_retires_old_coordinator()
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

        bool committed = false;
        await coordinator.RetireForIdentityChangeAsync(() => committed = true)
            .WaitAsync(TimeSpan.FromSeconds(3));
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(committed);
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
        Assert.False((await coordinator.TickAsync()).LaunchDispatched);
        Assert.Equal(0, launcher.Dispatches);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.StartNowAsync());
    }

    [Fact]
    public async Task Disable_while_waiting_for_launch_slot_does_not_dispatch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var queue = new BlockingLaunchGate();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy,
            store, new ConstantDiscovery(Detection.Absent()), launcher, clock, queue);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        clock.Elapsed = TimeSpan.FromSeconds(32);
        Task<CoordinatorResult> waiting = coordinator.TickAsync();
        await queue.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Task disable = coordinator.SetEnabledAsync(false);
        await Task.WhenAll(waiting, disable).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, launcher.Dispatches);
        Assert.False(store.Load(id).Checkpoint.Enabled);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
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
    public async Task Disable_and_reenable_preserve_lockout_without_dispatching_again()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var launcher = new FailingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id,
            AutoPolicy with { MaximumAutomaticAttempts = 1 }, store,
            new ConstantDiscovery(Detection.Absent()), launcher, clock);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);
        Assert.True(coordinator.Snapshot.LockedOut);

        await coordinator.SetEnabledAsync(false);
        RecoveryCheckpoint disabled = store.Load(id).Checkpoint;
        Assert.False(disabled.Enabled);
        Assert.True(disabled.LockedOut);
        Assert.Equal(1, disabled.ReservedAutomaticAttempts);

        await coordinator.SetEnabledAsync(true);
        Assert.Equal(RecoveryState.AwaitingIntervention, coordinator.Snapshot.State);
        await TickAt(coordinator, clock, 100);
        Assert.Equal(1, launcher.Dispatches);
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
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
    public async Task Detection_restoration_closes_gap_without_claiming_target_disappearance()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Present("session|app|100|start"));
        var recorder = new CapturingRecorder();
        using var coordinator = ProfileCoordinator.CreateNew(Guid.NewGuid(),
            RecoveryPolicy.Default, store, discovery, new CountingLauncher(), clock,
            recorder: recorder);

        await TickAt(coordinator, clock, 0);
        discovery.Result = Detection.Unavailable("Controlled permission failure");
        await TickAt(coordinator, clock, 5);
        Assert.True(coordinator.Snapshot.DetectionUnavailable);
        discovery.Result = Detection.Absent();
        await TickAt(coordinator, clock, 6);

        Assert.False(coordinator.Snapshot.DetectionUnavailable);
        Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.MonitoringGap);
        Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.MonitoringRestored);
        Assert.DoesNotContain(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.TargetDisappeared);
    }

    [Fact]
    public async Task Windows_interruption_discards_observation_credit_then_requires_fresh_discovery()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        RecoveryPolicy policy = RecoveryPolicy.Default with
        {
            ObservationPeriod = TimeSpan.FromSeconds(60)
        };
        using var coordinator = ProfileCoordinator.CreateNew(id, policy, store,
            new ConstantDiscovery(Detection.Present("session|app|100|start")),
            new CountingLauncher(), clock, recorder: recorder);

        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 5);
        clock.Elapsed = TimeSpan.FromSeconds(10);
        CoordinatorResult interrupted = await coordinator.MarkMonitoringInterruptedAsync();
        Assert.True(interrupted.Snapshot.DetectionUnavailable);
        Assert.Null(interrupted.Snapshot.ObservationStartedAt);
        Assert.Equal(0, interrupted.Snapshot.ReservedAutomaticAttempts);

        await TickAt(coordinator, clock, 10);
        Assert.Equal(TimeSpan.FromSeconds(10), coordinator.Snapshot.ObservationStartedAt);
        for (int second = 15; second <= 70; second += 5)
            await TickAt(coordinator, clock, second);
        Assert.Equal(RecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.MonitoringGap);
        Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.MonitoringRestored);
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
    public async Task Launch_failure_records_native_category_without_exception_message()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()),
            new ThrowingLauncher(new Win32Exception(5, "secret launch argument")),
            clock, recorder: recorder);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);

        OperationalEvent failure = Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.LaunchFailed);
        Assert.Equal(OperationalFailureCategory.PermissionDenied, failure.FailureCategory);
        Assert.Equal(5, failure.NativeErrorCode);
        Assert.DoesNotContain("secret launch argument", JsonSerializer.Serialize(failure));
        Assert.Equal(1, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
    }

    [Fact]
    public async Task Missing_executable_is_distinguished_from_activation_failure()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Absent()),
            new ThrowingLauncher(new FileNotFoundException("missing executable")),
            clock, recorder: recorder);
        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 2);
        await TickAt(coordinator, clock, 32);

        OperationalEvent failure = Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.LaunchFailed);
        Assert.Equal(OperationalFailureCategory.MissingTarget, failure.FailureCategory);
        Assert.Null(failure.NativeErrorCode);
    }

    [Fact]
    public async Task Unavailable_detection_records_native_category_without_dispatch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var recorder = new CapturingRecorder();
        var launcher = new CountingLauncher();
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, AutoPolicy, store,
            new ConstantDiscovery(Detection.Unavailable("candidate could not be read",
                DetectionFailureKind.PermissionDenied, 5)),
            launcher, clock, recorder: recorder);

        await TickAt(coordinator, clock, 0);
        await TickAt(coordinator, clock, 32);

        OperationalEvent unavailable = Assert.Single(recorder.Events, entry =>
            entry.Kind == OperationalEventKind.DetectionUnavailable);
        Assert.Equal(OperationalFailureCategory.PermissionDenied,
            unavailable.FailureCategory);
        Assert.Equal(5, unavailable.NativeErrorCode);
        Assert.DoesNotContain("candidate could not be read",
            JsonSerializer.Serialize(unavailable));
        Assert.Equal(0, launcher.Dispatches);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);
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

    [Fact]
    public async Task Stop_and_pause_commits_pause_before_graceful_request_and_requires_separate_force()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var stopper = new FakeStopper();
        var recorder = new CapturingRecorder();
        Guid id = Guid.NewGuid();
        stopper.OnGraceful = () => Assert.True(store.Load(id).Checkpoint.Paused);
        using var coordinator = ProfileCoordinator.CreateNew(id, RecoveryPolicy.Default,
            store, new ConstantDiscovery(Detection.Present("selected-instance")),
            new CountingLauncher(), clock, recorder: recorder, stopper: stopper);
        await TickAt(coordinator, clock, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ForceClosePausedAsync(Guid.NewGuid(), "selected-instance"));

        StopCommandResult first = await coordinator.StopAndPauseAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(TargetStopOutcome.NeedsForceChoice, first.Stop.Outcome);
        Assert.Equal("selected-instance", first.SelectedIdentity);
        Assert.Equal(1, stopper.GracefulCalls);
        Assert.Equal(0, stopper.ForceCalls);
        Assert.True(coordinator.Snapshot.Paused);
        Assert.Equal(0, store.Load(id).Checkpoint.ReservedAutomaticAttempts);

        TargetStopResult forced = await coordinator.ForceClosePausedAsync(
            first.OperationId, first.SelectedIdentity!);
        Assert.Equal(TargetStopOutcome.Stopped, forced.Outcome);
        Assert.Equal(1, stopper.ForceCalls);
        Assert.True(store.Load(id).Checkpoint.Paused);
        Assert.Contains(recorder.Events, entry => entry.Kind ==
            OperationalEventKind.ExplicitStopRequested &&
            entry.OperationId == first.OperationId);
        Assert.Contains(recorder.Events, entry => entry.Kind ==
            OperationalEventKind.ExplicitStopNeedsForceChoice &&
            entry.OperationId == first.OperationId);
        Assert.Contains(recorder.Events, entry => entry.Kind ==
            OperationalEventKind.ExplicitForceCloseRequested &&
            entry.OperationId == first.OperationId);
        Assert.Contains(recorder.Events, entry => entry.Kind ==
            OperationalEventKind.ExplicitStopCompleted &&
            entry.OperationId == first.OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ForceClosePausedAsync(first.OperationId,
                first.SelectedIdentity!));
        Assert.Equal(1, stopper.ForceCalls);
    }

    [Fact]
    public async Task Failed_pause_write_prevents_explicit_stop_request()
    {
        using var directory = new TestDirectory();
        var real = new RecoveryStateStore(directory.Path);
        var store = new SwitchableFailureStore(real);
        var stopper = new FakeStopper();
        var clock = new FakeClock();
        using var coordinator = ProfileCoordinator.CreateNew(Guid.NewGuid(),
            RecoveryPolicy.Default, store,
            new ConstantDiscovery(Detection.Present("selected-instance")),
            new CountingLauncher(), clock, stopper: stopper);
        await TickAt(coordinator, clock, 0);
        store.FailWrites = true;

        await Assert.ThrowsAsync<RecoveryStateUnavailableException>(() =>
            coordinator.StopAndPauseAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(0, stopper.GracefulCalls);
        Assert.Equal(0, stopper.ForceCalls);
    }

    [Fact]
    public async Task Explicit_restart_preserves_budget_and_reserves_manual_launch_after_fresh_absence()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Present("selected-instance"));
        var launcher = new CountingLauncher();
        var stopper = new FakeStopper { GracefulOutcome = TargetStopOutcome.Stopped };
        Guid id = Guid.NewGuid();
        using var coordinator = ProfileCoordinator.CreateNew(id, RecoveryPolicy.Default,
            store, discovery, launcher, clock, stopper: stopper);
        await TickAt(coordinator, clock, 0);
        StopCommandResult stopped = await coordinator.StopForRestartAsync(
            TimeSpan.FromSeconds(1));
        Assert.Equal(TargetStopOutcome.Stopped, stopped.Stop.Outcome);
        Assert.True(store.Load(id).Checkpoint.Paused);
        discovery.Result = Detection.Absent();

        CoordinatorResult launched = await coordinator.CompleteRestartAsync(
            stopped.OperationId, stopped.SelectedIdentity);

        Assert.True(launched.LaunchDispatched);
        Assert.Equal(1, launcher.Dispatches);
        RecoveryCheckpoint checkpoint = store.Load(id).Checkpoint;
        Assert.False(checkpoint.Paused);
        Assert.Equal(0, checkpoint.ReservedAutomaticAttempts);
        Assert.True(checkpoint.PendingExplicitStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CompleteRestartAsync(stopped.OperationId,
                stopped.SelectedIdentity));
    }

    [Fact]
    public async Task Restart_does_not_launch_when_a_matching_instance_appears_after_stop()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Present("selected-instance"));
        var launcher = new CountingLauncher();
        var stopper = new FakeStopper { GracefulOutcome = TargetStopOutcome.Stopped };
        using var coordinator = ProfileCoordinator.CreateNew(Guid.NewGuid(),
            RecoveryPolicy.Default, store, discovery, launcher, clock,
            stopper: stopper);
        await TickAt(coordinator, clock, 0);
        StopCommandResult stopped = await coordinator.StopForRestartAsync(
            TimeSpan.FromSeconds(1));
        discovery.Result = Detection.Present("new-instance");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CompleteRestartAsync(stopped.OperationId,
                stopped.SelectedIdentity));

        Assert.Equal(0, launcher.Dispatches);
        Assert.True(coordinator.Snapshot.Paused);
    }

    [Fact]
    public async Task Restart_requires_the_matching_force_choice_before_launch()
    {
        using var directory = new TestDirectory();
        var store = new RecoveryStateStore(directory.Path);
        var clock = new FakeClock();
        var discovery = new MutableDiscovery(Detection.Present("selected-instance"));
        var launcher = new CountingLauncher();
        var stopper = new FakeStopper();
        using var coordinator = ProfileCoordinator.CreateNew(Guid.NewGuid(),
            RecoveryPolicy.Default, store, discovery, launcher, clock,
            stopper: stopper);
        await TickAt(coordinator, clock, 0);
        StopCommandResult stopped = await coordinator.StopForRestartAsync(
            TimeSpan.FromSeconds(1));
        Assert.Equal(TargetStopOutcome.NeedsForceChoice, stopped.Stop.Outcome);
        discovery.Result = Detection.Absent();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.CompleteRestartAsync(stopped.OperationId,
                stopped.SelectedIdentity));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ForceClosePausedAsync(Guid.NewGuid(), "selected-instance"));
        Assert.Equal(0, stopper.ForceCalls);
        Assert.Equal(0, launcher.Dispatches);

        TargetStopResult forced = await coordinator.ForceClosePausedAsync(
            stopped.OperationId, stopped.SelectedIdentity!);
        Assert.Equal(TargetStopOutcome.Stopped, forced.Outcome);
        CoordinatorResult launched = await coordinator.CompleteRestartAsync(
            stopped.OperationId, stopped.SelectedIdentity);
        Assert.True(launched.LaunchDispatched);
        Assert.Equal(1, stopper.ForceCalls);
        Assert.Equal(1, launcher.Dispatches);
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

    private sealed class ThrowingLauncher(Exception error) : IProcessLauncher
    {
        public Task LaunchAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromException(error);
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

    private sealed class FakeStopper : IProcessStopper
    {
        public Action? OnGraceful { get; set; }
        public TargetStopOutcome GracefulOutcome { get; set; } =
            TargetStopOutcome.NeedsForceChoice;
        public int GracefulCalls { get; private set; }
        public int ForceCalls { get; private set; }
        public Task<TargetStopResult> TryGracefulCloseAsync(string selectedIdentity,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            GracefulCalls++;
            OnGraceful?.Invoke();
            return Task.FromResult(new TargetStopResult(GracefulOutcome));
        }
        public Task<TargetStopResult> ForceCloseAsync(string selectedIdentity,
            CancellationToken cancellationToken = default)
        {
            ForceCalls++;
            return Task.FromResult(new TargetStopResult(TargetStopOutcome.Stopped));
        }
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
