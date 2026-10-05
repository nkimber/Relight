using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Relight.Core;
using Relight.Storage;

namespace Relight.Engine;

public interface IMonotonicClock
{
    TimeSpan Elapsed { get; }
}

public sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

public interface IProcessDiscovery
{
    Task<Detection> DetectAsync(CancellationToken cancellationToken);
}

public interface IProcessLauncher
{
    /// <summary>Dispatches one launch; process appearance is verified by discovery later.</summary>
    Task LaunchAsync(Guid operationId, CancellationToken cancellationToken);
}

public enum TargetStopOutcome
{
    Stopped,
    AlreadyAbsent,
    NeedsForceChoice,
    IdentityChanged,
    Unavailable
}

public sealed record TargetStopResult(TargetStopOutcome Outcome, string? Reason = null);

public interface IProcessStopper
{
    Task<TargetStopResult> TryGracefulCloseAsync(string selectedIdentity,
        TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<TargetStopResult> ForceCloseAsync(string selectedIdentity,
        CancellationToken cancellationToken = default);
}

public sealed record StopCommandResult(TargetStopResult Stop,
    Guid OperationId, string? SelectedIdentity);

public sealed record CoordinatorResult(
    RecoverySnapshot Snapshot,
    RecoveryTransition Transition,
    bool LaunchDispatched,
    bool StorageDegraded,
    string? Error,
    bool LoggingDegraded,
    string? LoggingError);

/// <summary>
/// Serializes one profile's polling and commands. Other profiles own independent
/// coordinators, so a blocked launch here cannot hold a global policy lock.
/// </summary>
public sealed class ProfileCoordinator : IDisposable
{
    private readonly Guid _profileId;
    private readonly IRecoveryStateStore _store;
    private readonly RecoverySessionStateStore? _sessionStore;
    private readonly SharedRecoveryBudgetStore? _sharedBudget;
    private readonly IProcessDiscovery _discovery;
    private readonly IProcessLauncher _launcher;
    private readonly IProcessStopper? _stopper;
    private readonly ILaunchGate _launchGate;
    private readonly IEventRecorder _recorder;
    private readonly IMonotonicClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Detection? _lastDetection;
    private readonly object _launchSync = new();
    private CancellationTokenSource? _launchCancellation;
    private int _commandGeneration;
    private readonly RecoveryMachine _machine;
    private long _revision;
    private long _budgetRevision;
    private RecoveryCheckpoint _lastPersistedCheckpoint;
    private readonly HashSet<Guid> _activeLedgerOperations = [];
    private Guid? _interruptedLedgerOperation;
    private bool _interruptedLedgerOperationExplicit;
    private TimeSpan? _interruptedAbsentSince;
    private TimeSpan? _interruptedLastVerifiedAt;
    private volatile bool _reconciliationPending;
    private bool _storageDegraded;
    private string? _storageError;
    private bool _loggingDegraded;
    private string? _loggingError;
    private bool _storageEventEmitted;
    private volatile bool _identityRetired;
    private (Guid OperationId, string Identity, bool Restart)? _pendingForceChoice;
    private (Guid OperationId, string? Identity)? _pendingRestart;

    private ProfileCoordinator(Guid profileId, IRecoveryStateStore store,
        IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, RecoveryMachine machine, long revision,
        RecoveryCheckpoint persistedCheckpoint, ILaunchGate launchGate,
        IEventRecorder recorder, IProcessStopper? stopper,
        SharedRecoveryBudgetStore? sharedBudget)
    {
        _profileId = profileId;
        _store = store;
        _sessionStore = sharedBudget is null ? null : store as RecoverySessionStateStore ??
            throw new ArgumentException("Shared budgets require a session state store.", nameof(store));
        _sharedBudget = sharedBudget;
        _discovery = discovery;
        _launcher = launcher;
        _stopper = stopper;
        _launchGate = launchGate;
        _recorder = recorder;
        _clock = clock;
        _machine = machine;
        _revision = revision;
        _lastPersistedCheckpoint = persistedCheckpoint;
        _budgetRevision = persistedCheckpoint.SharedBudgetRevision ?? 0;
    }

    public static ProfileCoordinator CreateNew(Guid profileId, RecoveryPolicy policy,
        IRecoveryStateStore store, IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, ILaunchGate? launchGate = null,
        IEventRecorder? recorder = null, IProcessStopper? stopper = null,
        SharedRecoveryBudgetStore? sharedBudget = null)
    {
        var machine = new RecoveryMachine(policy);
        if (sharedBudget is not null)
        {
            if (store is not RecoverySessionStateStore)
                throw new ArgumentException("Shared budgets require a session state store.", nameof(store));
            sharedBudget.Create(profileId);
        }
        StoredRecoveryState initial = store.Create(profileId, machine.ExportCheckpoint());
        return new(profileId, store, discovery, launcher, clock, machine, initial.Revision,
            initial.Checkpoint,
            launchGate ?? UnboundedLaunchGate.Instance,
            recorder ?? NullEventRecorder.Instance, stopper, sharedBudget);
    }

    public static ProfileCoordinator OpenExisting(Guid profileId, RecoveryPolicy policy,
        IRecoveryStateStore store, IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, ILaunchGate? launchGate = null,
        IEventRecorder? recorder = null, IProcessStopper? stopper = null,
        SharedRecoveryBudgetStore? sharedBudget = null)
    {
        if (sharedBudget is not null && store is not RecoverySessionStateStore)
            throw new ArgumentException("Shared budgets require a session state store.", nameof(store));
        StoredRecoveryState saved = store.Load(profileId);
        var machine = RecoveryMachine.Restore(policy, saved.Checkpoint);
        var coordinator = new ProfileCoordinator(profileId, store, discovery, launcher,
            clock, machine, saved.Revision,
            saved.Checkpoint,
            launchGate ?? UnboundedLaunchGate.Instance,
            recorder ?? NullEventRecorder.Instance, stopper, sharedBudget);
        if (sharedBudget is not null)
            coordinator.IdentifyInterruptedLedgerOperation(saved.Checkpoint,
                sharedBudget.Load(profileId));
        return coordinator;
    }

    public RecoverySnapshot Snapshot => _machine.Snapshot;
    public Detection? LastDetection => Volatile.Read(ref _lastDetection);
    public RecoveryPolicy Policy => _machine.Policy;
    public bool StorageDegraded => _storageDegraded;
    public string? StorageError => _storageError;
    public bool LoggingDegraded => _loggingDegraded;
    public string? LoggingError => _loggingError;
    public bool ReconciliationPending => _reconciliationPending;

    private void IdentifyInterruptedLedgerOperation(RecoveryCheckpoint saved,
        SharedRecoveryBudget budget)
    {
        if (budget.Revision != _budgetRevision)
            throw new StaleRecoveryRevisionException(
                "The shared budget changed while session state was opened.");
        Guid? pending = budget.PendingAutomaticOperationId ??
            budget.PendingExplicitOperationId;
        if (pending is null) return;
        if (saved.LastState == RecoveryState.Starting &&
            saved.PendingOperationId == pending &&
            saved.PendingExplicitStart ==
                (budget.PendingExplicitOperationId is not null ? true : null))
        {
            _interruptedLedgerOperation = pending;
            _interruptedLedgerOperationExplicit =
                budget.PendingExplicitOperationId is not null;
            _reconciliationPending = true;
            return;
        }
        MarkStorageDegraded(new RecoveryStateUnavailableException(
            "A shared launch is pending in another session or lacks a matching local checkpoint; automatic actions remain suspended."));
    }

    private async Task<CoordinatorResult> ReconcileInterruptedLaunchAsync(
        CancellationToken cancellationToken)
    {
        if (_storageDegraded)
        {
            Detection passive = await Discover(cancellationToken).ConfigureAwait(false);
            RecoverySnapshot prior = _machine.Snapshot;
            RecoveryTransition observed = _machine.Advance(passive, _clock.Elapsed);
            RecordTransition(prior, _machine.Snapshot, passive);
            return Result(new(observed.Before, observed.After, RecoverySignal.None,
                "Shared recovery state is degraded; launch reconciliation is suspended"));
        }
        if (!_machine.Snapshot.Enabled || _machine.Snapshot.Paused)
        {
            _interruptedAbsentSince = null;
            _interruptedLastVerifiedAt = null;
            return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                RecoverySignal.None, "Interrupted launch reconciliation waits while protection is inactive"));
        }

        Detection found = await Discover(cancellationToken).ConfigureAwait(false);
        TimeSpan now = _clock.Elapsed;
        if (found.Kind == DetectionKind.Unavailable)
        {
            _interruptedAbsentSince = null;
            _interruptedLastVerifiedAt = null;
            RecoverySnapshot previous = _machine.Snapshot;
            RecoveryTransition unavailable = _machine.Advance(found, now);
            RecordTransition(previous, _machine.Snapshot, found);
            return Result(unavailable);
        }
        if (found.Kind == DetectionKind.Absent)
        {
            if (_interruptedLastVerifiedAt is { } lastVerified &&
                now - lastVerified >
                    _machine.Policy.ObservationPollInterval + TimeSpan.FromSeconds(5))
                _interruptedAbsentSince = null;
            _interruptedAbsentSince ??= now;
            _interruptedLastVerifiedAt = now;
            if (now - _interruptedAbsentSince < _machine.Policy.AppearanceTimeout)
                return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                    RecoverySignal.None,
                    "Waiting a full fresh appearance timeout for the interrupted launch"));
        }

        try
        {
            Guid operation = _interruptedLedgerOperation!.Value;
            SharedRecoveryBudget current = _sharedBudget!.Load(_profileId);
            if (current.Revision != _budgetRevision)
                throw new StaleRecoveryRevisionException(
                    "The shared budget changed during interrupted-launch reconciliation.");
            SharedRecoveryBudget resolved = _interruptedLedgerOperationExplicit
                ? _sharedBudget.ResolveExplicitStart(_profileId, current.Revision, operation)
                : _sharedBudget.ResolveAutomatic(_profileId, current.Revision, operation);
            _sessionStore!.AdoptCommittedBudget(resolved);
            _budgetRevision = resolved.Revision;
            _interruptedLedgerOperation = null;
            _interruptedAbsentSince = null;
            _interruptedLastVerifiedAt = null;
            _reconciliationPending = false;

            RecoverySnapshot before = _machine.Snapshot;
            RecoveryTransition transition = _machine.Advance(found, now);
            if (!Persist())
                return Result(new(before.State, _machine.Snapshot.State,
                    RecoverySignal.None, "Interrupted launch could not be saved"));
            RecordTransition(before, _machine.Snapshot, found);
            Record(OperationalEventKind.InterruptedLaunchReconciled,
                EventSeverity.Information, before, _machine.Snapshot, operation);
            return Result(transition);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            UnauthorizedAccessException)
        {
            MarkStorageDegraded(error);
            return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                RecoverySignal.None, "Interrupted launch could not be reconciled"));
        }
    }

    public async Task<CoordinatorResult> TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identityRetired)
                return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                    RecoverySignal.None, "This target identity was replaced"));
            if (_interruptedLedgerOperation is not null)
                return await ReconcileInterruptedLaunchAsync(cancellationToken)
                    .ConfigureAwait(false);
            int commandGeneration = Volatile.Read(ref _commandGeneration);
            RecoveryCheckpoint before = _machine.ExportCheckpoint();
            RecoverySnapshot previous = _machine.Snapshot;
            Detection found = await Discover(cancellationToken).ConfigureAwait(false);
            RecoveryTransition transition = _machine.Advance(found, _clock.Elapsed);
            bool persisted = PersistIfChanged(before);
            RecordTransition(previous, _machine.Snapshot, found);
            if (!persisted || _storageDegraded)
                return Result(transition);

            if (transition.Signal != RecoverySignal.LaunchDue)
                return Result(transition);

            using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool cancelBeforeDispatch;
            lock (_launchSync)
            {
                _launchCancellation = launchCancellation;
                cancelBeforeDispatch = commandGeneration != _commandGeneration;
            }
            if (cancelBeforeDispatch) launchCancellation.Cancel();
            Guid? reservedOperationId = null;
            try
            {
                // Queue before the final lookup and durable reservation. A target
                // may have appeared while this profile waited for a launch slot.
                using IDisposable permit = await _launchGate.EnterAsync(
                    launchCancellation.Token).ConfigureAwait(false);
                before = _machine.ExportCheckpoint();
                previous = _machine.Snapshot;
                found = await Discover(launchCancellation.Token).ConfigureAwait(false);
                transition = _machine.Advance(found, _clock.Elapsed);
                persisted = PersistIfChanged(before);
                RecordTransition(previous, _machine.Snapshot, found);
                if (!persisted || _storageDegraded ||
                    found.Kind != DetectionKind.Absent ||
                    transition.Signal != RecoverySignal.LaunchDue)
                    return Result(transition);

                Guid operationId = Guid.NewGuid();
                previous = _machine.Snapshot;
                _machine.ReserveAutomaticAttempt(_clock.Elapsed, operationId);
                if (!Persist())
                    return Result(new(RecoveryState.RetryWaiting, _machine.Snapshot.State,
                        RecoverySignal.None, "Reservation could not be committed"));
                reservedOperationId = operationId;
                RecordTransition(previous, _machine.Snapshot);
                Record(OperationalEventKind.LaunchReserved, EventSeverity.Information,
                    previous, _machine.Snapshot, operationId);
                // The reservation is durable. Confirm absence once more at the
                // dispatch boundary; a new instance or an inspection gap keeps
                // the attempt charged but must not create a duplicate launch.
                found = await Discover(launchCancellation.Token).ConfigureAwait(false);
                if (found.Kind != DetectionKind.Absent)
                {
                    before = _machine.ExportCheckpoint();
                    previous = _machine.Snapshot;
                    transition = _machine.Advance(found, _clock.Elapsed);
                    persisted = PersistIfChanged(before);
                    RecordTransition(previous, _machine.Snapshot, found);
                    Record(OperationalEventKind.LaunchAverted,
                        found.Kind == DetectionKind.Present ? EventSeverity.Information :
                            EventSeverity.Warning, previous, _machine.Snapshot,
                        operationId, DetectionCategory(found), found.NativeErrorCode);
                    return Result(new(RecoveryState.Starting, _machine.Snapshot.State,
                        RecoverySignal.None, persisted
                            ? "Reserved launch averted after final discovery"
                            : "Final discovery changed; session state could not be committed"));
                }
                launchCancellation.Token.ThrowIfCancellationRequested();
                await _launcher.LaunchAsync(operationId, launchCancellation.Token).ConfigureAwait(false);
                Record(OperationalEventKind.LaunchDispatched, EventSeverity.Information,
                    _machine.Snapshot, _machine.Snapshot, operationId);
                return Result(new(RecoveryState.RetryWaiting, RecoveryState.Starting,
                    RecoverySignal.None, "Reserved launch dispatched"), dispatched: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The reservation remains consumed. A future restart reports an
                // interrupted attempt; do not guess whether dispatch took effect.
                throw;
            }
            catch (OperationCanceledException) when (launchCancellation.IsCancellationRequested)
            {
                if (reservedOperationId is { } reserved)
                {
                    previous = _machine.Snapshot;
                    _machine.FailLaunch(reserved, _clock.Elapsed);
                    Persist();
                    RecordTransition(previous, _machine.Snapshot);
                    Record(OperationalEventKind.LaunchFailed, EventSeverity.Warning,
                        previous, _machine.Snapshot, reserved,
                        OperationalFailureCategory.Canceled);
                    return Result(new(RecoveryState.Starting, _machine.Snapshot.State,
                        RecoverySignal.None, "Launch canceled by profile command; reservation remains consumed"));
                }
                return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                    RecoverySignal.None, "Launch canceled before reservation"));
            }
            catch (Exception error)
            {
                if (reservedOperationId is { } reserved)
                {
                    previous = _machine.Snapshot;
                    _machine.FailLaunch(reserved, _clock.Elapsed);
                    Persist();
                    RecordTransition(previous, _machine.Snapshot);
                    Record(OperationalEventKind.LaunchFailed, EventSeverity.Error,
                        previous, _machine.Snapshot, reserved,
                        ClassifyLaunchFailure(error), NativeErrorCode(error));
                    return Result(new(RecoveryState.Starting, _machine.Snapshot.State,
                        RecoverySignal.None, $"Launch dispatch failed: {error.Message}"));
                }
                return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                    RecoverySignal.None, $"Launch queue failed: {error.Message}"));
            }
            finally
            {
                lock (_launchSync) _launchCancellation = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void CancelPendingLaunchForMonitoringInterruption()
    {
        CancellationTokenSource? pending;
        lock (_launchSync)
        {
            _commandGeneration++;
            pending = _launchCancellation;
        }
        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { /* Dispatch finished as the interruption arrived. */ }
    }

    public async Task<CoordinatorResult> MarkMonitoringInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        CancelPendingLaunchForMonitoringInterruption();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identityRetired)
                return Result(new(_machine.Snapshot.State, _machine.Snapshot.State,
                    RecoverySignal.None, "This target identity was replaced"));
            RecoveryCheckpoint before = _machine.ExportCheckpoint();
            RecoverySnapshot previous = _machine.Snapshot;
            Detection interruption = Detection.Unavailable(
                "Windows monitoring was interrupted; target presence must be checked again.");
            RecoveryTransition transition = _machine.Advance(interruption, _clock.Elapsed);
            PersistIfChanged(before);
            RecordTransition(previous, _machine.Snapshot, interruption);
            return Result(transition);
        }
        finally { _gate.Release(); }
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (paused)
        {
            CancellationTokenSource? pending;
            lock (_launchSync)
            {
                _commandGeneration++;
                pending = _launchCancellation;
            }
            try { pending?.Cancel(); }
            catch (ObjectDisposedException) { /* The launch completed just before cancellation. */ }
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException("Recovery state is degraded; policy change cannot be trusted.");
            if (!_machine.Snapshot.Enabled)
                throw new InvalidOperationException("Enable protection before changing its pause state.");
            RecoverySnapshot previous = _machine.Snapshot;
            _machine.SetPaused(paused);
            if (!paused)
            {
                _pendingForceChoice = null;
                _pendingRestart = null;
            }
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
            Record(paused ? OperationalEventKind.ProtectionPaused :
                    OperationalEventKind.ProtectionResumed, EventSeverity.Information,
                previous, _machine.Snapshot);
        }
        finally { _gate.Release(); }
    }

    public Task<StopCommandResult> StopAndPauseAsync(TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default) =>
        StopCoreAsync(gracefulTimeout, restart: false, cancellationToken);

    public Task<StopCommandResult> StopForRestartAsync(TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default) =>
        StopCoreAsync(gracefulTimeout, restart: true, cancellationToken);

    private async Task<StopCommandResult> StopCoreAsync(TimeSpan gracefulTimeout,
        bool restart, CancellationToken cancellationToken)
    {
        if (_stopper is null)
            throw new InvalidOperationException("This target has no verified stop adapter.");
        if (gracefulTimeout <= TimeSpan.Zero || gracefulTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(gracefulTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? pending;
        lock (_launchSync)
        {
            _commandGeneration++;
            pending = _launchCancellation;
        }
        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identityRetired)
                throw new InvalidOperationException("This target identity was replaced.");
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException(
                    "Recovery state is degraded; protection cannot be paused safely.");
            if (!_machine.Snapshot.Enabled)
                throw new InvalidOperationException("Enable protection before stopping the target.");
            if (restart && _machine.Snapshot.HoldReason is not null)
                throw new InvalidOperationException(
                    "An interrupted launch must be resolved before restarting this target.");
            RecoverySnapshot before = _machine.Snapshot;
            if (!before.Paused)
            {
                _machine.SetPaused(true);
                if (!Persist())
                    throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
                Record(OperationalEventKind.ProtectionPaused, EventSeverity.Information,
                    before, _machine.Snapshot);
            }
            Guid operationId = Guid.NewGuid();
            string? selectedIdentity = _machine.Snapshot.TargetIdentity;
            _pendingForceChoice = null;
            _pendingRestart = null;
            Record(OperationalEventKind.ExplicitStopRequested, EventSeverity.Information,
                before, _machine.Snapshot, operationId);
            TargetStopResult stop;
            if (selectedIdentity is null)
            {
                Detection found = await Discover(cancellationToken).ConfigureAwait(false);
                stop = found.Kind == DetectionKind.Absent
                    ? new(TargetStopOutcome.AlreadyAbsent)
                    : new(TargetStopOutcome.Unavailable,
                        "There is no previously verified selected instance to stop.");
            }
            else
                stop = await _stopper.TryGracefulCloseAsync(selectedIdentity,
                    gracefulTimeout, cancellationToken).ConfigureAwait(false);
            if (stop.Outcome == TargetStopOutcome.NeedsForceChoice &&
                selectedIdentity is not null)
                _pendingForceChoice = (operationId, selectedIdentity, restart);
            else if (restart && stop.Outcome is (TargetStopOutcome.Stopped or
                     TargetStopOutcome.AlreadyAbsent))
                _pendingRestart = (operationId, selectedIdentity);
            RecordStopOutcome(stop, operationId);
            return new(stop, operationId, selectedIdentity);
        }
        finally { _gate.Release(); }
    }

    public async Task<TargetStopResult> ForceClosePausedAsync(Guid operationId,
        string selectedIdentity, CancellationToken cancellationToken = default)
    {
        if (_stopper is null)
            throw new InvalidOperationException("This target has no verified stop adapter.");
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(selectedIdentity))
            throw new ArgumentException("A selected stop operation is required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identityRetired)
                throw new InvalidOperationException("This target identity was replaced.");
            if (_storageDegraded || !_machine.Snapshot.Enabled ||
                !_machine.Snapshot.Paused ||
                _pendingForceChoice is not { } choice ||
                choice.OperationId != operationId ||
                !string.Equals(choice.Identity, selectedIdentity,
                    StringComparison.Ordinal) ||
                !string.Equals(_machine.Snapshot.TargetIdentity, selectedIdentity,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The selected paused target changed; force close was not attempted.");
            _pendingForceChoice = null;
            Record(OperationalEventKind.ExplicitForceCloseRequested,
                EventSeverity.Warning, _machine.Snapshot, _machine.Snapshot, operationId);
            TargetStopResult stop = await _stopper.ForceCloseAsync(selectedIdentity,
                cancellationToken).ConfigureAwait(false);
            if (choice.Restart && stop.Outcome is (TargetStopOutcome.Stopped or
                TargetStopOutcome.AlreadyAbsent))
                _pendingRestart = (operationId, selectedIdentity);
            RecordStopOutcome(stop, operationId);
            return stop;
        }
        finally { _gate.Release(); }
    }

    public async Task<CoordinatorResult> CompleteRestartAsync(Guid operationId,
        string? selectedIdentity, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A verified restart operation is required.",
                nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storageDegraded || !_machine.Snapshot.Enabled ||
                !_machine.Snapshot.Paused ||
                _pendingRestart is not { } pending ||
                pending.OperationId != operationId ||
                !string.Equals(pending.Identity, selectedIdentity,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The verified restart operation is no longer available.");
            Detection found = await Discover(cancellationToken).ConfigureAwait(false);
            if (found.Kind != DetectionKind.Absent)
            {
                _pendingRestart = null;
                throw new InvalidOperationException(found.Kind == DetectionKind.Unavailable
                    ? $"Target identity cannot be verified: {found.Reason}"
                    : "A matching application is running; restart launch was not dispatched.");
            }
            RecoverySnapshot before = _machine.Snapshot;
            _machine.PrepareExplicitRestart(_clock.Elapsed);
            _pendingRestart = null;
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
            Record(OperationalEventKind.ExplicitRestartRequested,
                EventSeverity.Information, before, _machine.Snapshot, operationId);

            // Keep the profile gate until the explicit launch is reserved and
            // dispatched. A scheduler tick cannot consume an automatic attempt
            // between the verified stop and this user-authorized launch.
            return await StartNowUnderGateAsync(Volatile.Read(ref _commandGeneration),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void RecordStopOutcome(TargetStopResult stop, Guid operationId)
    {
        OperationalEventKind kind = stop.Outcome switch
        {
            TargetStopOutcome.Stopped or TargetStopOutcome.AlreadyAbsent =>
                OperationalEventKind.ExplicitStopCompleted,
            TargetStopOutcome.NeedsForceChoice =>
                OperationalEventKind.ExplicitStopNeedsForceChoice,
            _ => OperationalEventKind.ExplicitStopUnresolved
        };
        Record(kind, kind == OperationalEventKind.ExplicitStopCompleted
                ? EventSeverity.Information : EventSeverity.Warning,
            _machine.Snapshot, _machine.Snapshot, operationId);
    }

    public async Task SetEnabledAsync(bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
        {
            CancellationTokenSource? pending;
            lock (_launchSync)
            {
                _commandGeneration++;
                pending = _launchCancellation;
            }
            try { pending?.Cancel(); }
            catch (ObjectDisposedException) { /* Dispatch finished as disable was requested. */ }
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException(
                    "Recovery state is degraded; enablement cannot be trusted.");
            if (_machine.Snapshot.Enabled == enabled) return;
            if (!enabled)
            {
                _pendingForceChoice = null;
                _pendingRestart = null;
            }
            _machine.SetEnabled(enabled);
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Commits configuration while this profile is serialized, then applies the
    /// live policy. A rejected/stale configuration leaves the old policy active.
    /// The caller runs this command off the UI dispatcher.
    /// </summary>
    public async Task ApplyPolicyChangeAsync(RecoveryPolicy next, Action commitConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitConfiguration);
        next.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException(
                    "Recovery state is degraded; policy change cannot be trusted.");
            cancellationToken.ThrowIfCancellationRequested();
            RecoverySnapshot before = _machine.Snapshot;
            TimeSpan now = _clock.Elapsed;
            commitConfiguration();
            _machine.UpdatePolicy(next, now);
            Record(OperationalEventKind.PolicyChanged, EventSeverity.Information,
                before, _machine.Snapshot);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Cancels pending dispatch, serializes with the active target operation,
    /// then commits a replacement identity. A rejected commit leaves this
    /// coordinator usable; a successful one retires it without clearing budget.
    /// The host must remove it and attach a fresh adapter after commit.
    /// </summary>
    public async Task RetireForIdentityChangeAsync(Action commitConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitConfiguration);
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? pending;
        lock (_launchSync)
        {
            _commandGeneration++;
            pending = _launchCancellation;
        }
        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_identityRetired)
                throw new InvalidOperationException("This target identity was already replaced.");
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException(
                    "Recovery state is degraded; target identity cannot be changed safely.");
            if (_machine.Snapshot.State == RecoveryState.Starting ||
                _interruptedLedgerOperation is not null)
                throw new InvalidOperationException(
                    "Wait for the current launch to be reconciled before changing target identity.");
            cancellationToken.ThrowIfCancellationRequested();
            commitConfiguration();
            _pendingForceChoice = null;
            _pendingRestart = null;
            _identityRetired = true;
        }
        finally { _gate.Release(); }
    }

    public async Task ResetRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException("Recovery state is degraded; reset cannot be trusted.");
            if (_interruptedLedgerOperation is not null)
                throw new InvalidOperationException(
                    "Wait for interrupted launch reconciliation before resetting recovery.");
            if (!_machine.Snapshot.Enabled)
                throw new InvalidOperationException("Enable protection before resetting recovery.");
            if (_machine.Snapshot.State == RecoveryState.Starting)
                throw new InvalidOperationException("Wait for the current launch to finish before resetting recovery.");

            Detection found = await Discover(cancellationToken).ConfigureAwait(false);
            RecoverySnapshot previous = _machine.Snapshot;
            _machine.ResetRecovery(_clock.Elapsed);
            _machine.Advance(found, _clock.Elapsed);
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
            RecordTransition(previous, _machine.Snapshot, found);
            Record(OperationalEventKind.RecoveryReset, EventSeverity.Information,
                previous, _machine.Snapshot);
        }
        finally { _gate.Release(); }
    }

    public async Task<CoordinatorResult> StartNowAsync(
        CancellationToken cancellationToken = default)
    {
        int commandGeneration = Volatile.Read(ref _commandGeneration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StartNowUnderGateAsync(commandGeneration, cancellationToken)
            .ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<CoordinatorResult> StartNowUnderGateAsync(
        int commandGeneration, CancellationToken cancellationToken)
    {
        if (_identityRetired)
            throw new InvalidOperationException("This target identity was replaced.");
        if (_storageDegraded)
            throw new RecoveryStateUnavailableException("Recovery state is degraded; explicit launch is suspended.");
        if (_interruptedLedgerOperation is not null)
            throw new InvalidOperationException(
                "Wait for interrupted launch reconciliation before starting this application.");
        if (!_machine.Snapshot.Enabled || _machine.Snapshot.Paused)
            throw new InvalidOperationException("Resume protection before starting this application.");
        if (_machine.Snapshot.HoldReason == RecoveryHoldReason.InterruptedExplicitLaunch)
            throw new InvalidOperationException(
                "An interrupted explicit launch may still appear. Verify the target and reset recovery before starting again.");
        if (_machine.Snapshot.State == RecoveryState.Starting)
            throw new InvalidOperationException("A launch is already in progress.");

        using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        lock (_launchSync)
        {
            _launchCancellation = launchCancellation;
            if (commandGeneration != _commandGeneration)
                launchCancellation.Cancel();
        }
        try
        {
            using IDisposable permit = await _launchGate.EnterAsync(
                launchCancellation.Token).ConfigureAwait(false);
            Detection found = await Discover(launchCancellation.Token).ConfigureAwait(false);
            RecoveryCheckpoint before = _machine.ExportCheckpoint();
            RecoverySnapshot previous = _machine.Snapshot;
            RecoveryTransition transition = _machine.Advance(found, _clock.Elapsed);
            if (!PersistIfChanged(before))
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
            RecordTransition(previous, _machine.Snapshot, found);
            if (found.Kind == DetectionKind.Unavailable)
                throw new InvalidOperationException(
                    $"Target identity cannot be verified: {found.Reason}");
            if (found.Kind == DetectionKind.Present)
                return Result(new(previous.State, _machine.Snapshot.State,
                    RecoverySignal.None, "Existing matching instance adopted; no launch dispatched"));
            if (_machine.Snapshot.State is RecoveryState.Healthy or RecoveryState.Observing)
                throw new InvalidOperationException(
                    "Wait for target absence to be confirmed before starting another instance.");

            launchCancellation.Token.ThrowIfCancellationRequested();
            Guid operationId = Guid.NewGuid();
            previous = _machine.Snapshot;
            _machine.StartExplicitly(_clock.Elapsed, operationId);
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
            RecordTransition(previous, _machine.Snapshot);
            Record(OperationalEventKind.ExplicitStartRequested, EventSeverity.Information,
                previous, _machine.Snapshot, operationId);

            // A manual launch has the same duplicate-instance race as an
            // automatic one after its pending marker is committed.
            found = await Discover(launchCancellation.Token).ConfigureAwait(false);
            if (found.Kind != DetectionKind.Absent)
            {
                RecoveryCheckpoint reserved = _machine.ExportCheckpoint();
                previous = _machine.Snapshot;
                RecoveryTransition finalTransition = _machine.Advance(found, _clock.Elapsed);
                if (!PersistIfChanged(reserved))
                    throw new RecoveryStateUnavailableException(
                        _storageError ?? "Session state could not be committed.");
                RecordTransition(previous, _machine.Snapshot, found);
                Record(OperationalEventKind.ExplicitStartAverted,
                    found.Kind == DetectionKind.Present ? EventSeverity.Information :
                        EventSeverity.Warning, previous, _machine.Snapshot, operationId,
                    DetectionCategory(found), found.NativeErrorCode);
                return Result(new(finalTransition.Before, finalTransition.After,
                    RecoverySignal.None,
                    "Explicit launch averted after final discovery"));
            }

            bool enteredDispatch = false;
            try
            {
                launchCancellation.Token.ThrowIfCancellationRequested();
                enteredDispatch = true;
                await _launcher.LaunchAsync(operationId, launchCancellation.Token)
                    .ConfigureAwait(false);
                Record(OperationalEventKind.ExplicitStartDispatched, EventSeverity.Information,
                    _machine.Snapshot, _machine.Snapshot, operationId);
                return Result(new(previous.State, RecoveryState.Starting,
                    RecoverySignal.None, "Explicit launch dispatched"), dispatched: true);
            }
            catch (OperationCanceledException) when (!enteredDispatch &&
                launchCancellation.IsCancellationRequested)
            {
                previous = _machine.Snapshot;
                _machine.FailLaunch(operationId, _clock.Elapsed);
                if (!Persist())
                    throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
                RecordTransition(previous, _machine.Snapshot);
                throw;
            }
            catch (Exception error)
            {
                // The adapter may have dispatched before reporting failure.
                // Keep Starting until discovery or the full appearance timeout
                // resolves the uncertainty; do not authorize a duplicate now.
                Record(OperationalEventKind.ExplicitStartUncertain, EventSeverity.Warning,
                    _machine.Snapshot, _machine.Snapshot, operationId,
                    ClassifyLaunchFailure(error), NativeErrorCode(error));
                throw;
            }
        }
        finally
        {
            lock (_launchSync) _launchCancellation = null;
        }
    }

    private async Task<Detection> Discover(CancellationToken cancellationToken)
    {
        try
        {
            Detection found = await _discovery.DetectAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _lastDetection, found);
            return found;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Detection unavailable = Detection.Unavailable(error.Message,
                error is UnauthorizedAccessException or
                    Win32Exception { NativeErrorCode: 5 }
                    ? DetectionFailureKind.PermissionDenied
                    : DetectionFailureKind.InspectionFailed,
                NativeErrorCode(error));
            Volatile.Write(ref _lastDetection, unavailable);
            return unavailable;
        }
    }

    private bool PersistIfChanged(RecoveryCheckpoint before) =>
        before == _machine.ExportCheckpoint() || Persist();

    private bool Persist()
    {
        if (_storageDegraded) return false;
        try
        {
            RecoveryCheckpoint next = _machine.ExportCheckpoint();
            if (_sharedBudget is not null) CommitSharedBudget(next);
            RecoveryCheckpoint durable = _interruptedLedgerOperation is { } interrupted
                ? next with
                {
                    LastState = RecoveryState.Starting,
                    PendingOperationId = interrupted,
                    PendingExplicitStart = _interruptedLedgerOperationExplicit ? true : null
                }
                : next;
            StoredRecoveryState saved = _store.Save(_profileId, _revision, durable);
            _revision = saved.Revision;
            _lastPersistedCheckpoint = saved.Checkpoint;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MarkStorageDegraded(error);
            return false;
        }
    }

    private void MarkStorageDegraded(Exception error)
    {
        _storageDegraded = true;
        _reconciliationPending = false;
        _storageError = error.Message;
        if (_storageEventEmitted) return;
        _storageEventEmitted = true;
        Record(OperationalEventKind.StorageDegraded, EventSeverity.Error,
            _machine.Snapshot, _machine.Snapshot);
    }

    private void CommitSharedBudget(RecoveryCheckpoint next)
    {
        SharedRecoveryBudgetStore budgets = _sharedBudget!;
        RecoverySessionStateStore session = _sessionStore!;
        SharedRecoveryBudget current = budgets.Load(_profileId);
        if (current.Revision != _budgetRevision)
            throw new StaleRecoveryRevisionException(
                "The shared recovery budget changed in another session.");

        if (next.LastState == RecoveryState.Disabled &&
            current.PendingAutomaticOperationId is { } disabledAutomatic &&
            _activeLedgerOperations.Remove(disabledAutomatic))
        {
            _interruptedLedgerOperation = disabledAutomatic;
            _interruptedLedgerOperationExplicit = false;
            _interruptedAbsentSince = null;
            _interruptedLastVerifiedAt = null;
            _reconciliationPending = true;
        }
        if (next.LastState == RecoveryState.Disabled &&
            current.PendingExplicitOperationId is { } disabledExplicit &&
            _activeLedgerOperations.Remove(disabledExplicit))
        {
            _interruptedLedgerOperation = disabledExplicit;
            _interruptedLedgerOperationExplicit = true;
            _interruptedAbsentSince = null;
            _interruptedLastVerifiedAt = null;
            _reconciliationPending = true;
        }

        void Adopt(SharedRecoveryBudget committed)
        {
            session.AdoptCommittedBudget(committed);
            _budgetRevision = committed.Revision;
            current = committed;
        }

        bool clearsBudget = next.ReservedAutomaticAttempts == 0 &&
            next.EpisodeId is null && !next.LockedOut &&
            (current.ReservedAutomaticAttempts > 0 || current.EpisodeId is not null ||
             current.LockedOut);
        if (clearsBudget)
        {
            bool stable = _lastPersistedCheckpoint.LastState == RecoveryState.Observing &&
                next.LastState == RecoveryState.Healthy;
            if (stable)
            {
                if (current.EpisodeId is not { } episode)
                    throw new RecoveryStateUnavailableException(
                        "Stable observation cannot reconcile a shared budget without an episode.");
                Adopt(budgets.CompleteStableObservation(_profileId,
                    current.Revision, episode));
            }
            else
                Adopt(budgets.ResetExplicitly(_profileId, current.Revision));
            return;
        }

        if (current.PendingAutomaticOperationId is { } automatic &&
            _activeLedgerOperations.Contains(automatic) &&
            _lastPersistedCheckpoint.LastState == RecoveryState.Starting &&
            next.LastState is RecoveryState.Observing or RecoveryState.RetryWaiting or
                RecoveryState.AwaitingIntervention)
        {
            Adopt(budgets.ResolveAutomatic(_profileId, current.Revision, automatic));
            _activeLedgerOperations.Remove(automatic);
        }
        if (current.PendingExplicitOperationId is { } explicitOperation &&
            _activeLedgerOperations.Contains(explicitOperation) &&
            _lastPersistedCheckpoint.LastState == RecoveryState.Starting &&
            next.LastState is RecoveryState.Observing or RecoveryState.RetryWaiting or
                RecoveryState.AwaitingIntervention)
        {
            Adopt(budgets.ResolveExplicitStart(_profileId, current.Revision,
                explicitOperation));
            _activeLedgerOperations.Remove(explicitOperation);
        }

        if (current.EpisodeId is null && next.EpisodeId is { } newEpisode &&
            next.ReservedAutomaticAttempts == current.ReservedAutomaticAttempts)
            Adopt(budgets.BeginEpisode(_profileId, current.Revision, newEpisode));

        if (next.ReservedAutomaticAttempts == current.ReservedAutomaticAttempts + 1)
        {
            if (next.PendingOperationId is not { } automaticOperation ||
                next.PendingExplicitStart == true || next.EpisodeId is not { } episode)
                throw new RecoveryStateUnavailableException(
                    "An automatic reservation lacks its operation or episode identity.");
            Adopt(budgets.ReserveAutomatic(_profileId, current.Revision,
                _machine.Policy.MaximumAutomaticAttempts, episode, automaticOperation));
            _activeLedgerOperations.Add(automaticOperation);
        }
        else if (next.ReservedAutomaticAttempts != current.ReservedAutomaticAttempts)
            throw new StaleRecoveryRevisionException(
                "The local automatic attempt count does not match the shared budget.");

        if (next.LastState == RecoveryState.Starting &&
            next.PendingExplicitStart == true &&
            next.PendingOperationId is { } explicitStart &&
            current.PendingExplicitOperationId != explicitStart)
        {
            Adopt(budgets.MarkExplicitStart(_profileId, current.Revision, explicitStart));
            _activeLedgerOperations.Add(explicitStart);
        }

        if (next.LockedOut && !current.LockedOut &&
            current.EpisodeId is { } lockedEpisode &&
            current.PendingAutomaticOperationId is null)
            Adopt(budgets.EnterLockout(_profileId, current.Revision, lockedEpisode));
    }

    private void RecordTransition(RecoverySnapshot before, RecoverySnapshot after,
        Detection? detection = null)
    {
        if (before.State != after.State)
            Record(OperationalEventKind.StateChanged, EventSeverity.Information, before, after);
        if (!before.DetectionUnavailable && after.DetectionUnavailable)
        {
            OperationalFailureCategory? category = DetectionCategory(detection);
            Record(OperationalEventKind.DetectionUnavailable, EventSeverity.Warning,
                before, after, failureCategory: category,
                nativeErrorCode: detection?.NativeErrorCode);
            Record(OperationalEventKind.MonitoringGap, EventSeverity.Warning,
                before, after, failureCategory: category,
                nativeErrorCode: detection?.NativeErrorCode);
        }
        if (before.DetectionUnavailable && !after.DetectionUnavailable)
            Record(OperationalEventKind.MonitoringRestored, EventSeverity.Information, before, after);
        if (before.TargetIdentity != after.TargetIdentity && after.TargetIdentity is not null)
            Record(OperationalEventKind.TargetObserved, EventSeverity.Information, before, after);
        if (before.State != RecoveryState.Observing && after.State == RecoveryState.Observing)
            Record(OperationalEventKind.ObservationStarted, EventSeverity.Information, before, after);
        if (before.State == RecoveryState.Observing && after.State == RecoveryState.Healthy)
        {
            Record(OperationalEventKind.ObservationCompleted, EventSeverity.Information, before, after);
            if (before.LockedOut && !after.LockedOut)
                Record(OperationalEventKind.RecoveryRearmed, EventSeverity.Information, before, after);
        }
        if ((before.State is RecoveryState.Healthy or RecoveryState.Observing) &&
            (after.State is RecoveryState.RetryWaiting or RecoveryState.AwaitingIntervention))
            Record(OperationalEventKind.TargetDisappeared, EventSeverity.Warning, before, after);
        if (before.State == RecoveryState.Observing &&
            ((after.State != RecoveryState.Observing &&
              after.State != RecoveryState.Healthy) ||
             (after.State == RecoveryState.Observing &&
              after.ObservationStartedAt is null)))
            Record(OperationalEventKind.ObservationInterrupted, EventSeverity.Warning, before, after,
                failureCategory: before.ObservationOrigin == ObservationOrigin.AutomaticLaunch &&
                    after.TargetIdentity is null ? OperationalFailureCategory.EarlyExit : null);
        if (!before.LockedOut && after.LockedOut)
            Record(OperationalEventKind.LockoutEntered, EventSeverity.Warning, before, after);
        if (before.State == RecoveryState.Starting && !before.DetectionUnavailable &&
            before.AppearanceDeadline is { } appearanceDeadline &&
            _clock.Elapsed >= appearanceDeadline && after.State != RecoveryState.Starting &&
            after.TargetIdentity is null)
            Record(OperationalEventKind.AppearanceTimedOut, EventSeverity.Warning, before, after,
                failureCategory: OperationalFailureCategory.AppearanceTimeout);
    }

    private static OperationalFailureCategory? DetectionCategory(Detection? detection) =>
        detection?.FailureKind switch
        {
            DetectionFailureKind.Ambiguous => OperationalFailureCategory.DetectionAmbiguous,
            DetectionFailureKind.PermissionDenied => OperationalFailureCategory.PermissionDenied,
            DetectionFailureKind.InspectionFailed => OperationalFailureCategory.DetectionFailed,
            DetectionFailureKind.ConfigurationChanged =>
                OperationalFailureCategory.ConfigurationChanged,
            DetectionFailureKind.Unknown => OperationalFailureCategory.Unknown,
            _ => null
        };

    private void Record(OperationalEventKind kind, EventSeverity severity,
        RecoverySnapshot before, RecoverySnapshot after, Guid? operationId = null,
        OperationalFailureCategory? failureCategory = null, int? nativeErrorCode = null)
    {
        var entry = new OperationalEvent(DateTimeOffset.UtcNow, Guid.NewGuid(), severity, kind,
            ProfileId: _profileId, EpisodeId: after.EpisodeId ?? before.EpisodeId,
            OperationId: operationId ?? after.OperationId ?? before.OperationId,
            PreviousState: before.State, NewState: after.State,
            Origin: after.ObservationOrigin ?? before.ObservationOrigin,
            AttemptNumber: after.ReservedAutomaticAttempts,
            AttemptLimit: _machine.Policy.MaximumAutomaticAttempts,
            ProcessIdentity: after.TargetIdentity ?? before.TargetIdentity,
            NativeErrorCode: nativeErrorCode, FailureCategory: failureCategory);
        try
        {
            if (!_recorder.TryRecord(entry))
            {
                _loggingDegraded = true;
                _loggingError = "Event queue is full; some events were dropped.";
            }
        }
        catch (Exception error)
        {
            _loggingDegraded = true;
            _loggingError = $"Event recorder failed ({error.GetType().Name}).";
        }
    }

    private static OperationalFailureCategory ClassifyLaunchFailure(Exception error) =>
        error switch
        {
            FileNotFoundException => OperationalFailureCategory.MissingTarget,
            DirectoryNotFoundException or ArgumentException =>
                OperationalFailureCategory.InvalidConfiguration,
            UnauthorizedAccessException => OperationalFailureCategory.PermissionDenied,
            OperationCanceledException => OperationalFailureCategory.Canceled,
            Win32Exception { NativeErrorCode: 2 or 3 } =>
                OperationalFailureCategory.MissingTarget,
            Win32Exception { NativeErrorCode: 267 } =>
                OperationalFailureCategory.InvalidConfiguration,
            Win32Exception { NativeErrorCode: 5 or 740 } =>
                OperationalFailureCategory.PermissionDenied,
            COMException { ErrorCode: unchecked((int)0x80070005) } =>
                OperationalFailureCategory.PermissionDenied,
            Win32Exception => OperationalFailureCategory.ActivationFailed,
            COMException => OperationalFailureCategory.ActivationFailed,
            _ => OperationalFailureCategory.Unknown
        };

    private static int? NativeErrorCode(Exception error) => error switch
    {
        Win32Exception windows => windows.NativeErrorCode,
        COMException com => com.ErrorCode,
        _ => null
    };

    private CoordinatorResult Result(RecoveryTransition transition, bool dispatched = false) =>
        new(_machine.Snapshot, transition, dispatched, _storageDegraded, _storageError,
            _loggingDegraded, _loggingError);

    public void Dispose() => _gate.Dispose();
}
