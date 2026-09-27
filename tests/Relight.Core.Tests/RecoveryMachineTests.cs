using Relight.Core;

namespace Relight.Core.Tests;

public sealed class RecoveryMachineTests
{
    [Fact]
    public void Explicit_restart_preserves_lockout_and_does_not_charge_automatic_budget()
    {
        Guid episode = Guid.NewGuid();
        var machine = RecoveryMachine.Restore(RecoveryPolicy.Default,
            new RecoveryCheckpoint(true, true, true, true, 3, episode,
                RecoveryState.AwaitingIntervention, null));

        machine.PrepareExplicitRestart(TimeSpan.Zero);
        machine.StartExplicitly(TimeSpan.Zero, Guid.NewGuid());

        Assert.True(machine.Snapshot.LockedOut);
        Assert.Equal(3, machine.Snapshot.ReservedAutomaticAttempts);
        Assert.Equal(episode, machine.Snapshot.EpisodeId);
        Assert.Equal(RecoveryState.Starting, machine.Snapshot.State);
        Assert.Equal(ObservationOrigin.ExplicitStart,
            machine.Snapshot.ObservationOrigin);
    }

    private static readonly Detection Target = Detection.Present("session-1:path:pid-42:start-100");
    private static readonly Detection OtherInstance = Detection.Present("session-1:path:pid-77:start-200");
    private static readonly Detection Missing = Detection.Absent();

    [Fact]
    public void Existing_target_is_adopted_without_launch_and_becomes_healthy()
    {
        var machine = new RecoveryMachine(RecoveryPolicy.Default);
        Assert.Equal(RecoverySignal.None, machine.Advance(Target, TimeSpan.Zero).Signal);
        Assert.Equal(ObservationOrigin.InitialAdoption, machine.Snapshot.ObservationOrigin);
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        Observe(machine, Target, 0, 600);
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
        Assert.Equal(0, machine.Snapshot.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Default_absent_profile_waits_for_first_external_start()
    {
        var machine = new RecoveryMachine(RecoveryPolicy.Default);
        machine.Advance(Missing, TimeSpan.Zero);
        machine.Advance(Missing, TimeSpan.FromHours(1));
        Assert.Equal(RecoveryState.WaitingForFirstStart, machine.Snapshot.State);
        Assert.Equal(RecoverySignal.None, machine.Advance(Target, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)).Signal);
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
    }

    [Fact]
    public void Healthy_outage_requires_absence_confirmation_then_retry_and_fresh_check()
    {
        var machine = HealthyMachine();
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
        machine.Advance(Missing, S(601));
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
        machine.Advance(Missing, S(603));
        Assert.Equal(RecoveryState.RetryWaiting, machine.Snapshot.State);
        Assert.Equal(RecoverySignal.None, machine.Advance(Missing, S(632)).Signal);
        // An externally started target wins the race over the launch deadline.
        machine.Advance(Target, S(633));
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        Assert.Equal(0, machine.Snapshot.ReservedAutomaticAttempts);
        Assert.Throws<InvalidOperationException>(() => machine.ReserveAutomaticAttempt(S(634), Guid.NewGuid()));
    }

    [Fact]
    public void Three_failed_dispatches_exhaust_exact_budget_and_fourth_is_rejected()
    {
        var machine = HealthyMachine();
        machine.Advance(Missing, S(601));
        machine.Advance(Missing, S(603));
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            TimeSpan due = S(633 + (attempt - 1) * 34);
            machine.Advance(Missing, due - S(2));
            Assert.Equal(RecoverySignal.LaunchDue, machine.Advance(Missing, due).Signal);
            Guid operation = Guid.NewGuid();
            var reserved = machine.ReserveAutomaticAttempt(due, operation);
            Assert.Equal(attempt, reserved.ReservedAutomaticAttempts);
            machine.FailLaunch(operation, due + S(1));
            Assert.Equal(attempt == 3 ? RecoveryState.AwaitingIntervention : RecoveryState.RetryWaiting,
                machine.Snapshot.State);
        }

        Assert.True(machine.Snapshot.LockedOut);
        Assert.Equal(3, machine.Snapshot.ReservedAutomaticAttempts);
        Assert.Equal(RecoverySignal.None, machine.Advance(Missing, S(800)).Signal);
        Assert.Throws<InvalidOperationException>(() => machine.ReserveAutomaticAttempt(S(801), Guid.NewGuid()));
    }

    [Fact]
    public void Third_attempt_gets_its_full_observation_and_can_reset_budget()
    {
        var machine = ExhaustTwoAttempts();
        var third = Guid.NewGuid();
        machine.Advance(Missing, S(699));
        machine.Advance(Missing, S(701));
        machine.ReserveAutomaticAttempt(S(701), third);
        Assert.Equal(3, machine.Snapshot.ReservedAutomaticAttempts);
        Assert.False(machine.Snapshot.LockedOut);
        machine.Advance(OtherInstance, S(702));
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        Assert.Equal(ObservationOrigin.AutomaticLaunch, machine.Snapshot.ObservationOrigin);
        Observe(machine, OtherInstance, 702, 1301);
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        machine.Advance(OtherInstance, S(1302));
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
        Assert.Equal(0, machine.Snapshot.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Early_exit_after_late_observation_does_not_reset_attempts()
    {
        var machine = HealthyMachine();
        machine.Advance(Missing, S(601));
        machine.Advance(Missing, S(603));
        machine.Advance(Missing, S(631));
        machine.Advance(Missing, S(633));
        machine.ReserveAutomaticAttempt(S(633), Guid.NewGuid());
        machine.Advance(OtherInstance, S(634));
        Observe(machine, OtherInstance, 634, 1233);
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        machine.Advance(Missing, S(1234));
        machine.Advance(Missing, S(1236));
        Assert.Equal(RecoveryState.RetryWaiting, machine.Snapshot.State);
        Assert.Equal(1, machine.Snapshot.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Stable_external_start_after_lockout_rearms_but_early_exit_does_not()
    {
        var machine = ExhaustThreeAttempts();
        machine.Advance(OtherInstance, S(801));
        Assert.True(machine.Snapshot.LockedOut);
        machine.Advance(Missing, S(804));
        machine.Advance(Missing, S(806));
        Assert.Equal(RecoveryState.AwaitingIntervention, machine.Snapshot.State);
        Assert.Equal(3, machine.Snapshot.ReservedAutomaticAttempts);
        machine.Advance(OtherInstance, S(807));
        Observe(machine, OtherInstance, 807, 1407);
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
        Assert.False(machine.Snapshot.LockedOut);
        Assert.Equal(0, machine.Snapshot.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Auto_rearm_disabled_keeps_stable_external_start_suspended()
    {
        var machine = new RecoveryMachine(RecoveryPolicy.Default with
        {
            StartAutomaticallyWhenInitiallyAbsent = true,
            MaximumAutomaticAttempts = 0,
            RearmAfterStableExternalStart = false
        });
        machine.Advance(Missing, S(0));
        machine.Advance(Missing, S(2));
        Assert.True(machine.Snapshot.LockedOut);
        machine.Advance(Target, S(3));
        Observe(machine, Target, 3, 603);
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
        Assert.True(machine.Snapshot.LockedOut);
        machine.Advance(Missing, S(604));
        machine.Advance(Missing, S(606));
        Assert.Equal(RecoveryState.AwaitingIntervention, machine.Snapshot.State);
        machine.ResetRecovery(S(607));
        Assert.False(machine.Snapshot.LockedOut);
    }

    [Fact]
    public void Unavailable_detection_and_pause_break_observation_continuity()
    {
        var machine = new RecoveryMachine(RecoveryPolicy.Default);
        machine.Advance(Target, S(0));
        machine.Advance(Detection.Unavailable("Access denied"), S(500));
        Assert.True(machine.Snapshot.DetectionUnavailable);
        machine.Advance(Target, S(501));
        machine.Advance(Target, S(1000));
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        machine.SetPaused(true);
        machine.Advance(Target, S(1200));
        machine.SetPaused(false);
        machine.Advance(Target, S(1201));
        Observe(machine, Target, 1201, 1800);
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        machine.Advance(Target, S(1801));
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
    }

    [Fact]
    public void Explicit_start_does_not_charge_budget_or_clear_lockout_on_appearance()
    {
        var machine = ExhaustThreeAttempts();
        Guid operation = Guid.NewGuid();
        machine.StartExplicitly(S(800), operation);
        machine.Advance(OtherInstance, S(801));
        Assert.Equal(ObservationOrigin.ExplicitStart, machine.Snapshot.ObservationOrigin);
        Assert.True(machine.Snapshot.LockedOut);
        Assert.Equal(3, machine.Snapshot.ReservedAutomaticAttempts);
    }

    [Fact]
    public void Changing_limit_does_not_unlock_and_stale_completion_cannot_apply()
    {
        var machine = ExhaustThreeAttempts();
        machine.UpdatePolicy(machine.Policy with { MaximumAutomaticAttempts = 5 }, S(800));
        Assert.True(machine.Snapshot.LockedOut);
        Assert.Throws<InvalidOperationException>(() => machine.ReserveAutomaticAttempt(S(801), Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => machine.FailLaunch(Guid.NewGuid(), S(801)));
    }

    [Fact]
    public void In_flight_launch_uses_reserved_policy_until_its_result()
    {
        var machine = HealthyMachine();
        machine.Advance(Missing, S(601));
        machine.Advance(Missing, S(603));
        Guid operation = Guid.NewGuid();
        machine.ReserveAutomaticAttempt(S(633), operation);
        TimeSpan originalDeadline = machine.Snapshot.AppearanceDeadline!.Value;

        machine.UpdatePolicy(machine.Policy with
        {
            MaximumAutomaticAttempts = 1,
            RetryDelay = S(5),
            AppearanceTimeout = S(5)
        }, S(634));
        Assert.Equal(originalDeadline, machine.Snapshot.AppearanceDeadline);
        machine.FailLaunch(operation, S(635));
        Assert.False(machine.Snapshot.LockedOut);
        Assert.Equal(S(665), machine.Snapshot.RetryDeadline);
        Assert.Equal(1, machine.Snapshot.ReservedAutomaticAttempts);
        Assert.Equal(RecoverySignal.None, machine.Advance(Missing, S(665)).Signal);
        Assert.True(machine.Snapshot.LockedOut);
    }

    [Fact]
    public void Live_observation_and_retry_edits_restart_their_respective_timers()
    {
        var observing = new RecoveryMachine(RecoveryPolicy.Default);
        observing.Advance(Target, S(0));
        Observe(observing, Target, 0, 100);
        observing.UpdatePolicy(observing.Policy with
        {
            ObservationPeriod = S(60)
        }, S(100));
        Assert.Equal(S(100), observing.Snapshot.ObservationStartedAt);
        Observe(observing, Target, 100, 160);
        Assert.Equal(RecoveryState.Healthy, observing.Snapshot.State);

        var retry = HealthyMachine();
        retry.Advance(Missing, S(601));
        retry.Advance(Missing, S(603));
        retry.UpdatePolicy(retry.Policy with { RetryDelay = S(5) }, S(610));
        Assert.Equal(S(615), retry.Snapshot.RetryDeadline);
        Assert.Equal(RecoverySignal.LaunchDue, retry.Advance(Missing, S(615)).Signal);
    }

    [Fact]
    public void Monitoring_gap_does_not_count_as_stable_observation()
    {
        var machine = new RecoveryMachine(RecoveryPolicy.Default);
        machine.Advance(Target, S(0));
        machine.Advance(Target, S(600));
        Assert.Equal(RecoveryState.Observing, machine.Snapshot.State);
        Assert.Equal(S(600), machine.Snapshot.ObservationStartedAt);
        Observe(machine, Target, 600, 1200);
        Assert.Equal(RecoveryState.Healthy, machine.Snapshot.State);
    }

    [Fact]
    public void Reducing_limit_during_retry_locks_out_without_dispatch()
    {
        var machine = HealthyMachine();
        machine.Advance(Missing, S(601));
        machine.Advance(Missing, S(603));
        machine.Advance(Missing, S(633));
        Guid operation = Guid.NewGuid();
        machine.ReserveAutomaticAttempt(S(633), operation);
        machine.FailLaunch(operation, S(634));
        machine.UpdatePolicy(machine.Policy with { MaximumAutomaticAttempts = 1 }, S(635));
        Assert.Equal(RecoverySignal.None, machine.Advance(Missing, S(665)).Signal);
        Assert.Equal(RecoveryState.AwaitingIntervention, machine.Snapshot.State);
        Assert.True(machine.Snapshot.LockedOut);
    }

    [Fact]
    public void One_reserved_operation_prevents_a_second_dispatch()
    {
        var machine = HealthyMachine();
        machine.Advance(Missing, S(601));
        machine.Advance(Missing, S(603));
        machine.Advance(Missing, S(633));
        machine.ReserveAutomaticAttempt(S(633), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() =>
            machine.ReserveAutomaticAttempt(S(633), Guid.NewGuid()));
        Assert.Equal(1, machine.Snapshot.ReservedAutomaticAttempts);
    }

    private static RecoveryMachine HealthyMachine()
    {
        var machine = new RecoveryMachine(RecoveryPolicy.Default);
        machine.Advance(Target, S(0));
        Observe(machine, Target, 0, 600);
        return machine;
    }

    private static RecoveryMachine ExhaustTwoAttempts()
    {
        var machine = HealthyMachine();
        machine.Advance(Missing, S(601));
        machine.Advance(Missing, S(603));
        for (int i = 0; i < 2; i++)
        {
            TimeSpan due = S(633 + i * 34);
            machine.Advance(Missing, due - S(2));
            machine.Advance(Missing, due);
            Guid operation = Guid.NewGuid();
            machine.ReserveAutomaticAttempt(due, operation);
            machine.FailLaunch(operation, due + S(1));
        }
        return machine;
    }

    private static RecoveryMachine ExhaustThreeAttempts()
    {
        var machine = ExhaustTwoAttempts();
        machine.Advance(Missing, S(699));
        machine.Advance(Missing, S(701));
        Guid operation = Guid.NewGuid();
        machine.ReserveAutomaticAttempt(S(701), operation);
        machine.FailLaunch(operation, S(702));
        return machine;
    }

    private static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);

    private static void Observe(RecoveryMachine machine, Detection target, int from, int through)
    {
        for (int second = from + 5; second <= through; second += 5)
            machine.Advance(target, S(second));
        if ((through - from) % 5 != 0)
            machine.Advance(target, S(through));
    }
}
