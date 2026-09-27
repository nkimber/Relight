namespace Relight.Core;

/// <summary>
/// Pure per-profile policy model. The caller supplies monotonic elapsed time and
/// authoritative discovery. It must serialize calls and durably save a new attempt
/// snapshot before dispatching any automatic launch.
/// </summary>
public sealed class RecoveryMachine
{
    private RecoveryPolicy _policy;
    private TimeSpan _lastTime;

    public RecoveryMachine(RecoveryPolicy policy, bool enabled = true)
    {
        policy.Validate();
        _policy = policy;
        Snapshot = new(
            enabled ? RecoveryState.WaitingForFirstStart : RecoveryState.Disabled,
            enabled, false, false, false, false, 0, null, null,
            null, null, null, null, null, null, null);
    }

    public RecoveryPolicy Policy => _policy;
    public RecoverySnapshot Snapshot { get; private set; }

    public RecoveryCheckpoint ExportCheckpoint() => new(
        Snapshot.Enabled, Snapshot.Paused, Snapshot.Armed, Snapshot.LockedOut,
        Snapshot.ReservedAutomaticAttempts, Snapshot.EpisodeId,
        Snapshot.State, Snapshot.OperationId,
        Snapshot.State == RecoveryState.Starting &&
            Snapshot.ObservationOrigin == ObservationOrigin.ExplicitStart ? true : null,
        Snapshot.HoldReason);

    public static RecoveryMachine Restore(RecoveryPolicy policy, RecoveryCheckpoint checkpoint)
    {
        policy.Validate();
        if (checkpoint.ReservedAutomaticAttempts is < 0 or > 20 ||
            (checkpoint.LastState == RecoveryState.Starting && checkpoint.PendingOperationId is null))
            throw new ArgumentException("Recovery checkpoint is invalid.", nameof(checkpoint));

        var machine = new RecoveryMachine(policy, checkpoint.Enabled);
        bool uncertainExplicit = checkpoint.LastState == RecoveryState.Starting &&
            checkpoint.PendingExplicitStart == true;
        bool exhausted = checkpoint.LockedOut || uncertainExplicit ||
            (checkpoint.LastState == RecoveryState.Starting &&
             checkpoint.ReservedAutomaticAttempts >= policy.MaximumAutomaticAttempts);
        RecoveryState state = !checkpoint.Enabled
            ? RecoveryState.Disabled
            : exhausted ? RecoveryState.AwaitingIntervention
            : !checkpoint.Armed ? RecoveryState.WaitingForFirstStart
            : RecoveryState.RetryWaiting;
        machine.Snapshot = machine.Snapshot with
        {
            State = state,
            Paused = checkpoint.Paused,
            Armed = checkpoint.Armed,
            LockedOut = exhausted,
            HoldReason = uncertainExplicit
                ? RecoveryHoldReason.InterruptedExplicitLaunch : checkpoint.HoldReason,
            ReservedAutomaticAttempts = checkpoint.ReservedAutomaticAttempts,
            EpisodeId = checkpoint.EpisodeId,
            // A persisted PID, observation interval or pending deadline proves nothing
            // after restart. The caller must discover again before taking action.
            OperationId = null,
            TargetIdentity = null,
            ObservationStartedAt = null,
            LastVerifiedAt = null,
            AbsenceStartedAt = null,
            RetryDeadline = null,
            AppearanceDeadline = null
        };
        return machine;
    }

    public RecoveryTransition Advance(Detection detection, TimeSpan now)
    {
        CheckTime(now);
        RecoveryState before = Snapshot.State;
        if (!Snapshot.Enabled || Snapshot.Paused)
            return Changed(before, "Policy actions suspended");

        if (detection.Kind == DetectionKind.Unavailable)
        {
            // A gap cannot count as observation, absence, or a reason to dispatch.
            Snapshot = Snapshot with
            {
                DetectionUnavailable = true,
                ObservationStartedAt = null,
                LastVerifiedAt = null,
                AbsenceStartedAt = null
            };
            return Changed(before, detection.Reason ?? "Detection unavailable");
        }

        if (Snapshot.DetectionUnavailable)
        {
            Snapshot = Snapshot with
            {
                DetectionUnavailable = false,
                ObservationStartedAt = null,
                LastVerifiedAt = null,
                AbsenceStartedAt = null
            };
            if (Snapshot.State == RecoveryState.Starting)
            {
                // An unobserved appearance cannot be attributed confidently to
                // this launch operation, but its reserved attempt stays charged.
                Snapshot = Snapshot with
                {
                    State = RecoveryState.RetryWaiting,
                    OperationId = null,
                    AppearanceDeadline = null,
                    RetryDeadline = null
                };
            }
        }

        return detection.Kind == DetectionKind.Present
            ? OnPresent(before, detection.Identity!, now)
            : OnAbsent(before, now);
    }

    public RecoverySnapshot ReserveAutomaticAttempt(TimeSpan now, Guid operationId)
    {
        CheckTime(now);
        if (operationId == Guid.Empty || !Snapshot.Enabled || Snapshot.Paused ||
            Snapshot.DetectionUnavailable || Snapshot.LockedOut ||
            Snapshot.State != RecoveryState.RetryWaiting ||
            Snapshot.RetryDeadline is null || now < Snapshot.RetryDeadline ||
            Snapshot.ReservedAutomaticAttempts >= _policy.MaximumAutomaticAttempts)
            throw new InvalidOperationException("Automatic launch is not currently allowed.");

        Snapshot = Snapshot with
        {
            State = RecoveryState.Starting,
            Armed = true,
            OperationId = operationId,
            ObservationOrigin = ObservationOrigin.AutomaticLaunch,
            ReservedAutomaticAttempts = Snapshot.ReservedAutomaticAttempts + 1,
            AppearanceDeadline = now + _policy.AppearanceTimeout,
            RetryDeadline = null,
            AbsenceStartedAt = null
        };
        return Snapshot;
    }

    public void StartExplicitly(TimeSpan now, Guid operationId)
    {
        CheckTime(now);
        if (operationId == Guid.Empty || !Snapshot.Enabled || Snapshot.Paused ||
            Snapshot.DetectionUnavailable || Snapshot.State == RecoveryState.Starting ||
            Snapshot.State == RecoveryState.Observing || Snapshot.State == RecoveryState.Healthy)
            throw new InvalidOperationException("Explicit launch is not currently allowed.");

        Snapshot = Snapshot with
        {
            State = RecoveryState.Starting,
            Armed = true,
            OperationId = operationId,
            ObservationOrigin = ObservationOrigin.ExplicitStart,
            AppearanceDeadline = now + _policy.AppearanceTimeout,
            RetryDeadline = null,
            AbsenceStartedAt = null
        };
    }

    public void FailLaunch(Guid operationId, TimeSpan now)
    {
        CheckTime(now);
        if (Snapshot.State != RecoveryState.Starting || Snapshot.OperationId != operationId)
            throw new InvalidOperationException("Stale launch completion.");
        FailCurrentAttempt(now);
    }

    public void SetPaused(bool paused)
    {
        Snapshot = Snapshot with
        {
            Paused = paused,
            ObservationStartedAt = paused ? null : Snapshot.ObservationStartedAt,
            LastVerifiedAt = paused ? null : Snapshot.LastVerifiedAt,
            AbsenceStartedAt = paused ? null : Snapshot.AbsenceStartedAt
        };
        if (paused && Snapshot.State == RecoveryState.RetryWaiting)
            Snapshot = Snapshot with { RetryDeadline = null };
    }

    public void SetEnabled(bool enabled)
    {
        Snapshot = Snapshot with
        {
            Enabled = enabled,
            State = enabled
                ? Snapshot.LockedOut ? RecoveryState.AwaitingIntervention
                    : Snapshot.Armed ? RecoveryState.RetryWaiting
                    : RecoveryState.WaitingForFirstStart
                : RecoveryState.Disabled,
            OperationId = null,
            AppearanceDeadline = null,
            RetryDeadline = null,
            AbsenceStartedAt = null,
            ObservationStartedAt = null,
            LastVerifiedAt = null
        };
    }

    public void ResetRecovery(TimeSpan now)
    {
        CheckTime(now);
        if (Snapshot.State == RecoveryState.Starting)
            throw new InvalidOperationException("Cannot reset while a launch is in flight.");
        Snapshot = Snapshot with
        {
            LockedOut = false,
            ReservedAutomaticAttempts = 0,
            EpisodeId = null,
            HoldReason = null,
            OperationId = null,
            TargetIdentity = null,
            ObservationOrigin = null,
            ObservationStartedAt = null,
            LastVerifiedAt = null,
            AbsenceStartedAt = null,
            AppearanceDeadline = null,
            RetryDeadline = null,
            State = Snapshot.Enabled
                ? Snapshot.Armed ? RecoveryState.RetryWaiting : RecoveryState.WaitingForFirstStart
                : RecoveryState.Disabled
        };
    }

    public void UpdatePolicy(RecoveryPolicy next, TimeSpan now)
    {
        next.Validate();
        CheckTime(now);
        if (Snapshot.State == RecoveryState.Observing &&
            next.ObservationPeriod != _policy.ObservationPeriod)
            Snapshot = Snapshot with { ObservationStartedAt = now };
        if (Snapshot.State == RecoveryState.RetryWaiting && next.RetryDelay != _policy.RetryDelay)
            Snapshot = Snapshot with { RetryDeadline = now + next.RetryDelay };
        _policy = next;
        // An in-flight appearance deadline and an existing lockout are retained.
    }

    private RecoveryTransition OnPresent(RecoveryState before, string identity, TimeSpan now)
    {
        bool same = Snapshot.TargetIdentity == identity;
        if (Snapshot.State == RecoveryState.Healthy && same)
            return Changed(before, "Target remains healthy");

        if (Snapshot.State == RecoveryState.Observing && same)
        {
            if (Snapshot.LastVerifiedAt is null ||
                now - Snapshot.LastVerifiedAt > _policy.ObservationPollInterval + TimeSpan.FromSeconds(5))
            {
                Snapshot = Snapshot with { ObservationStartedAt = now, LastVerifiedAt = now };
                return Changed(before, "Observation restarted after monitoring gap");
            }
            if (Snapshot.ObservationStartedAt is { } since &&
                now - since >= _policy.ObservationPeriod)
            {
                bool rearm = !Snapshot.LockedOut || _policy.RearmAfterStableExternalStart;
                Snapshot = Snapshot with
                {
                    State = RecoveryState.Healthy,
                    Armed = true,
                    LockedOut = !rearm,
                    HoldReason = rearm ? null : Snapshot.HoldReason,
                    ReservedAutomaticAttempts = rearm ? 0 : Snapshot.ReservedAutomaticAttempts,
                    EpisodeId = rearm ? null : Snapshot.EpisodeId,
                    ObservationStartedAt = null,
                    LastVerifiedAt = now,
                    OperationId = null,
                    AppearanceDeadline = null
                };
                return Changed(before, rearm ? "Observation complete; protection armed" :
                    "Running stably; recovery remains suspended");
            }
            if (Snapshot.ObservationStartedAt is null)
                Snapshot = Snapshot with { ObservationStartedAt = now };
            Snapshot = Snapshot with { LastVerifiedAt = now };
            return Changed(before, "Observation continues");
        }

        ObservationOrigin origin = before switch
        {
            RecoveryState.Starting when Snapshot.OperationId is not null &&
                Snapshot.AppearanceDeadline is not null =>
                Snapshot.ObservationOrigin == ObservationOrigin.ExplicitStart
                    ? ObservationOrigin.ExplicitStart
                    : ObservationOrigin.AutomaticLaunch,
            RecoveryState.WaitingForFirstStart when !Snapshot.Armed => ObservationOrigin.InitialAdoption,
            _ => ObservationOrigin.ExternalStart
        };

        Snapshot = Snapshot with
        {
            State = RecoveryState.Observing,
            Armed = true,
            TargetIdentity = identity,
            ObservationOrigin = origin,
            ObservationStartedAt = now,
            LastVerifiedAt = now,
            AbsenceStartedAt = null,
            RetryDeadline = null,
            AppearanceDeadline = null
        };
        return Changed(before, "Target discovered; observation started");
    }

    private RecoveryTransition OnAbsent(RecoveryState before, TimeSpan now)
    {
        if (Snapshot.State == RecoveryState.Starting)
        {
            if (Snapshot.AppearanceDeadline is { } deadline && now >= deadline)
            {
                FailCurrentAttempt(now);
                return Changed(before, "Launch appearance timeout");
            }
            return Changed(before, "Waiting for launched target");
        }

        if (Snapshot.State == RecoveryState.WaitingForFirstStart &&
            !_policy.StartAutomaticallyWhenInitiallyAbsent)
            return Changed(before, "Waiting for first start");

        if (Snapshot.State == RecoveryState.AwaitingIntervention)
            return Changed(before, "Waiting for external start");

        if (Snapshot.State == RecoveryState.RetryWaiting && Snapshot.RetryDeadline is { } retryDeadline)
        {
            if (Snapshot.LockedOut ||
                Snapshot.ReservedAutomaticAttempts >= _policy.MaximumAutomaticAttempts)
            {
                Snapshot = Snapshot with { State = RecoveryState.AwaitingIntervention, LockedOut = true,
                    RetryDeadline = null };
                return Changed(before, "Automatic recovery budget exhausted");
            }
            // The outage was already confirmed when this retry was scheduled.
            // This call itself is the fresh presence check before dispatch.
            return retryDeadline <= now
                ? new(before, Snapshot.State, RecoverySignal.LaunchDue, "Fresh absence confirmed; launch due")
                : Changed(before, "Waiting for recovery deadline");
        }

        if (Snapshot.AbsenceStartedAt is null)
        {
            Snapshot = Snapshot with { AbsenceStartedAt = now };
            return Changed(before, "Confirming target absence");
        }
        if (now - Snapshot.AbsenceStartedAt < _policy.AbsenceConfirmationDelay)
            return Changed(before, "Confirming target absence");

        if (Snapshot.State == RecoveryState.Observing)
        {
            bool lockedExternal = Snapshot.LockedOut;
            if (lockedExternal)
            {
                Snapshot = Snapshot with
                {
                    State = RecoveryState.AwaitingIntervention,
                    ObservationStartedAt = null,
                    LastVerifiedAt = null,
                    AbsenceStartedAt = null,
                    TargetIdentity = null
                };
                return Changed(before, "External start ended before stability; lockout retained");
            }
        }

        if (Snapshot.LockedOut || Snapshot.ReservedAutomaticAttempts >= _policy.MaximumAutomaticAttempts)
        {
            Snapshot = Snapshot with
            {
                State = RecoveryState.AwaitingIntervention,
                LockedOut = true,
                ObservationStartedAt = null,
                LastVerifiedAt = null,
                TargetIdentity = null,
                AbsenceStartedAt = null
            };
            return Changed(before, "Automatic recovery budget exhausted");
        }

        if (Snapshot.State != RecoveryState.RetryWaiting || Snapshot.RetryDeadline is null)
        {
            Snapshot = Snapshot with
            {
                State = RecoveryState.RetryWaiting,
                Armed = true,
                EpisodeId = Snapshot.EpisodeId ?? Guid.NewGuid(),
                RetryDeadline = now + _policy.RetryDelay,
                ObservationStartedAt = null,
                LastVerifiedAt = null,
                TargetIdentity = null,
                AbsenceStartedAt = null
            };
            return Changed(before, "Recovery retry scheduled");
        }

        return Snapshot.RetryDeadline <= now
            ? new(before, Snapshot.State, RecoverySignal.LaunchDue, "Fresh absence confirmed; launch due")
            : Changed(before, "Waiting for recovery deadline");
    }

    private void FailCurrentAttempt(TimeSpan now)
    {
        bool lockout = Snapshot.LockedOut ||
            Snapshot.ReservedAutomaticAttempts >= _policy.MaximumAutomaticAttempts;
        Snapshot = Snapshot with
        {
            State = lockout ? RecoveryState.AwaitingIntervention : RecoveryState.RetryWaiting,
            LockedOut = lockout,
            Armed = true,
            EpisodeId = Snapshot.EpisodeId ?? Guid.NewGuid(),
            OperationId = null,
            AppearanceDeadline = null,
            ObservationStartedAt = null,
            LastVerifiedAt = null,
            AbsenceStartedAt = null,
            TargetIdentity = null,
            RetryDeadline = lockout ? null : now + _policy.RetryDelay
        };
    }

    private RecoveryTransition Changed(RecoveryState before, string reason) =>
        new(before, Snapshot.State, RecoverySignal.None, reason);

    private void CheckTime(TimeSpan now)
    {
        if (now < _lastTime) throw new ArgumentOutOfRangeException(nameof(now), "Monotonic time moved backward.");
        _lastTime = now;
    }
}
