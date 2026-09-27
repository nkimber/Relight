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
    string? Error);

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
    private readonly IMonotonicClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _launchSync = new();
    private CancellationTokenSource? _launchCancellation;
    private int _commandGeneration;
    private readonly RecoveryMachine _machine;
    private long _revision;
    private bool _storageDegraded;
    private string? _storageError;

    private ProfileCoordinator(Guid profileId, IRecoveryStateStore store,
        IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock, RecoveryMachine machine, long revision)
    {
        _profileId = profileId;
        _store = store;
        _discovery = discovery;
        _launcher = launcher;
        _clock = clock;
        _machine = machine;
        _revision = revision;
    }

    public static ProfileCoordinator CreateNew(Guid profileId, RecoveryPolicy policy,
        IRecoveryStateStore store, IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock)
    {
        var machine = new RecoveryMachine(policy);
        StoredRecoveryState initial = store.Create(profileId, machine.ExportCheckpoint());
        return new(profileId, store, discovery, launcher, clock, machine, initial.Revision);
    }

    public static ProfileCoordinator OpenExisting(Guid profileId, RecoveryPolicy policy,
        IRecoveryStateStore store, IProcessDiscovery discovery, IProcessLauncher launcher,
        IMonotonicClock clock)
    {
        StoredRecoveryState saved = store.Load(profileId);
        var machine = RecoveryMachine.Restore(policy, saved.Checkpoint);
        return new(profileId, store, discovery, launcher, clock, machine, saved.Revision);
    }

    public RecoverySnapshot Snapshot => _machine.Snapshot;
    public bool StorageDegraded => _storageDegraded;
    public string? StorageError => _storageError;

    public async Task<CoordinatorResult> TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecoveryCheckpoint before = _machine.ExportCheckpoint();
            Detection found = await Discover(cancellationToken).ConfigureAwait(false);
            RecoveryTransition transition = _machine.Advance(found, _clock.Elapsed);
            if (!PersistIfChanged(before) || _storageDegraded)
                return Result(transition);

            if (transition.Signal != RecoverySignal.LaunchDue)
                return Result(transition);

            // Reconcile again immediately before reservation/dispatch. This is
            // deliberately separate from the poll that made the launch due.
            before = _machine.ExportCheckpoint();
            found = await Discover(cancellationToken).ConfigureAwait(false);
            transition = _machine.Advance(found, _clock.Elapsed);
            if (!PersistIfChanged(before) || _storageDegraded || found.Kind != DetectionKind.Absent ||
                transition.Signal != RecoverySignal.LaunchDue)
                return Result(transition);

            Guid operationId = Guid.NewGuid();
            int commandGeneration = Volatile.Read(ref _commandGeneration);
            _machine.ReserveAutomaticAttempt(_clock.Elapsed, operationId);
            if (!Persist())
                return Result(new(RecoveryState.RetryWaiting, _machine.Snapshot.State,
                    RecoverySignal.None, "Reservation could not be committed"));

            using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool cancelBeforeDispatch;
            lock (_launchSync)
            {
                _launchCancellation = launchCancellation;
                cancelBeforeDispatch = commandGeneration != _commandGeneration;
            }
            if (cancelBeforeDispatch) launchCancellation.Cancel();
            try
            {
                await _launcher.LaunchAsync(operationId, launchCancellation.Token).ConfigureAwait(false);
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
                _machine.FailLaunch(operationId, _clock.Elapsed);
                Persist();
                return Result(new(RecoveryState.Starting, _machine.Snapshot.State,
                    RecoverySignal.None, "Launch canceled by profile command; reservation remains consumed"));
            }
            catch (Exception error)
            {
                _machine.FailLaunch(operationId, _clock.Elapsed);
                Persist();
                return Result(new(RecoveryState.Starting, _machine.Snapshot.State,
                    RecoverySignal.None, $"Launch dispatch failed: {error.Message}"));
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
            _machine.SetPaused(paused);
            if (!Persist())
                throw new RecoveryStateUnavailableException(_storageError ?? "State write failed.");
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
            return false;
        }
    }

    private CoordinatorResult Result(RecoveryTransition transition, bool dispatched = false) =>
        new(_machine.Snapshot, transition, dispatched, _storageDegraded, _storageError);

    public void Dispose() => _gate.Dispose();
}
