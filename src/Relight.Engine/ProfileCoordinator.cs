using System.Diagnostics;
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
    private readonly IProcessDiscovery _discovery;
    private readonly IProcessLauncher _launcher;
    private readonly ILaunchGate _launchGate;
    private readonly IEventRecorder _recorder;
    private readonly IMonotonicClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _launchSync = new();
    private CancellationTokenSource? _launchCancellation;
    private int _commandGeneration;
    private readonly RecoveryMachine _machine;
    private long _revision;
    private bool _storageDegraded;
    private string? _storageError;
    private bool _loggingDegraded;
    private string? _loggingError;
    private bool _storageEventEmitted;

    private ProfileCoordinator(Guid profileId, IRecoveryStateStore store,
        IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, RecoveryMachine machine, long revision,
        ILaunchGate launchGate, IEventRecorder recorder)
    {
        _profileId = profileId;
        _store = store;
        _discovery = discovery;
        _launcher = launcher;
        _launchGate = launchGate;
        _recorder = recorder;
        _clock = clock;
        _machine = machine;
        _revision = revision;
    }

    public static ProfileCoordinator CreateNew(Guid profileId, RecoveryPolicy policy,
        IRecoveryStateStore store, IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, ILaunchGate? launchGate = null,
        IEventRecorder? recorder = null)
    {
        var machine = new RecoveryMachine(policy);
        StoredRecoveryState initial = store.Create(profileId, machine.ExportCheckpoint());
        return new(profileId, store, discovery, launcher, clock, machine, initial.Revision,
            launchGate ?? UnboundedLaunchGate.Instance,
            recorder ?? NullEventRecorder.Instance);
    }

    public static ProfileCoordinator OpenExisting(Guid profileId, RecoveryPolicy policy,
        IRecoveryStateStore store, IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, ILaunchGate? launchGate = null,
        IEventRecorder? recorder = null)
    {
        StoredRecoveryState saved = store.Load(profileId);
        var machine = RecoveryMachine.Restore(policy, saved.Checkpoint);
        return new(profileId, store, discovery, launcher, clock, machine, saved.Revision,
            launchGate ?? UnboundedLaunchGate.Instance,
            recorder ?? NullEventRecorder.Instance);
    }

    public RecoverySnapshot Snapshot => _machine.Snapshot;
    public bool StorageDegraded => _storageDegraded;
    public string? StorageError => _storageError;
    public bool LoggingDegraded => _loggingDegraded;
    public string? LoggingError => _loggingError;

    public async Task<CoordinatorResult> TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int commandGeneration = Volatile.Read(ref _commandGeneration);
            RecoveryCheckpoint before = _machine.ExportCheckpoint();
            RecoverySnapshot previous = _machine.Snapshot;
            Detection found = await Discover(cancellationToken).ConfigureAwait(false);
            RecoveryTransition transition = _machine.Advance(found, _clock.Elapsed);
            bool persisted = PersistIfChanged(before);
            RecordTransition(previous, _machine.Snapshot);
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
                RecordTransition(previous, _machine.Snapshot);
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
                        previous, _machine.Snapshot, reserved);
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
                        previous, _machine.Snapshot, reserved);
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
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
            Record(paused ? OperationalEventKind.ProtectionPaused :
                    OperationalEventKind.ProtectionResumed, EventSeverity.Information,
                previous, _machine.Snapshot);
        }
        finally { _gate.Release(); }
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
            _machine.SetEnabled(enabled);
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
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
            RecordTransition(previous, _machine.Snapshot);
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
        try
        {
            if (_storageDegraded)
                throw new RecoveryStateUnavailableException("Recovery state is degraded; explicit launch is suspended.");
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
                RecordTransition(previous, _machine.Snapshot);
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
                catch (Exception)
                {
                    // The adapter may have dispatched before reporting failure.
                    // Keep Starting until discovery or the full appearance timeout
                    // resolves the uncertainty; do not authorize a duplicate now.
                    Record(OperationalEventKind.ExplicitStartUncertain, EventSeverity.Warning,
                        _machine.Snapshot, _machine.Snapshot, operationId);
                    throw;
                }
            }
            finally
            {
                lock (_launchSync) _launchCancellation = null;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<Detection> Discover(CancellationToken cancellationToken)
    {
        try { return await _discovery.DetectAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Detection.Unavailable(error.Message); }
    }

    private bool PersistIfChanged(RecoveryCheckpoint before) =>
        before == _machine.ExportCheckpoint() || Persist();

    private bool Persist()
    {
        if (_storageDegraded) return false;
        try
        {
            _revision = _store.Save(_profileId, _revision, _machine.ExportCheckpoint()).Revision;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _storageDegraded = true;
            _storageError = error.Message;
            if (!_storageEventEmitted)
            {
                _storageEventEmitted = true;
                Record(OperationalEventKind.StorageDegraded, EventSeverity.Error,
                    _machine.Snapshot, _machine.Snapshot);
            }
            return false;
        }
    }

    private void RecordTransition(RecoverySnapshot before, RecoverySnapshot after)
    {
        if (before.State != after.State)
            Record(OperationalEventKind.StateChanged, EventSeverity.Information, before, after);
        if (!before.DetectionUnavailable && after.DetectionUnavailable)
        {
            Record(OperationalEventKind.DetectionUnavailable, EventSeverity.Warning, before, after);
            Record(OperationalEventKind.MonitoringGap, EventSeverity.Warning, before, after);
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
            (after.State != RecoveryState.Observing || after.ObservationStartedAt is null))
            Record(OperationalEventKind.ObservationInterrupted, EventSeverity.Warning, before, after);
        if (!before.LockedOut && after.LockedOut)
            Record(OperationalEventKind.LockoutEntered, EventSeverity.Warning, before, after);
        if (before.State == RecoveryState.Starting && !before.DetectionUnavailable &&
            before.AppearanceDeadline is { } appearanceDeadline &&
            _clock.Elapsed >= appearanceDeadline && after.State != RecoveryState.Starting &&
            after.TargetIdentity is null)
            Record(OperationalEventKind.AppearanceTimedOut, EventSeverity.Warning, before, after);
    }

    private void Record(OperationalEventKind kind, EventSeverity severity,
        RecoverySnapshot before, RecoverySnapshot after, Guid? operationId = null)
    {
        var entry = new OperationalEvent(DateTimeOffset.UtcNow, Guid.NewGuid(), severity, kind,
            ProfileId: _profileId, EpisodeId: after.EpisodeId ?? before.EpisodeId,
            OperationId: operationId ?? after.OperationId ?? before.OperationId,
            PreviousState: before.State, NewState: after.State,
            Origin: after.ObservationOrigin ?? before.ObservationOrigin,
            AttemptNumber: after.ReservedAutomaticAttempts,
            AttemptLimit: _machine.Policy.MaximumAutomaticAttempts,
            ProcessIdentity: after.TargetIdentity ?? before.TargetIdentity);
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

    private CoordinatorResult Result(RecoveryTransition transition, bool dispatched = false) =>
        new(_machine.Snapshot, transition, dispatched, _storageDegraded, _storageError,
            _loggingDegraded, _loggingError);

    public void Dispose() => _gate.Dispose();
}
