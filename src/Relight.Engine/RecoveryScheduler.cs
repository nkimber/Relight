using Relight.Core;

namespace Relight.Engine;

/// <summary>
/// One shared timer scans monotonic per-profile deadlines. Due ticks run
/// independently, so a slow adapter cannot block other profiles or the timer.
/// Pulse is also a deterministic test/harness entry point.
/// </summary>
public sealed class RecoveryScheduler : IAsyncDisposable
{
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<Guid, ScheduledProfile> _profiles = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;

    public RecoveryScheduler(IMonotonicClock clock) => _clock = clock;

    public void Add(Guid id, ProfileCoordinator coordinator, RecoveryPolicy policy)
    {
        if (id == Guid.Empty) throw new ArgumentException("Profile ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_profiles.TryAdd(id, new(coordinator, policy, _clock.Elapsed)))
                throw new InvalidOperationException("Profile is already scheduled.");
        }
    }

    public IReadOnlyDictionary<Guid, Task> Pulse()
    {
        var started = new Dictionary<Guid, Task>();
        lock (_sync)
        {
            if (_stop.IsCancellationRequested || _disposed) return started;
            TimeSpan now = _clock.Elapsed;
            foreach ((Guid id, ScheduledProfile profile) in _profiles)
            {
                if (profile.InFlight is not null || now < profile.NextDue) continue;
                profile.ImmediateRequested = false;
                // Task.Run prevents a synchronously blocked adapter from holding
                // the scheduler's scan lock or delaying another due profile.
                profile.InFlight = Task.Run(() => TickAsync(id, profile), CancellationToken.None);
                started.Add(id, profile.InFlight);
            }
        }
        return started;
    }

    public void RequestImmediate(Guid id)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_profiles.TryGetValue(id, out ScheduledProfile? profile)) return;
            if (profile.InFlight is null) profile.NextDue = _clock.Elapsed;
            else profile.ImmediateRequested = true;
        }
    }

    public async Task RemoveAsync(Guid id)
    {
        ScheduledProfile? profile;
        lock (_sync)
        {
            if (!_profiles.Remove(id, out profile)) return;
        }
        profile.Cancellation.Cancel();
        if (profile.InFlight is { } running) await running.ConfigureAwait(false);
        profile.Coordinator.Dispose();
        profile.Cancellation.Dispose();
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_loop is not null) throw new InvalidOperationException("Scheduler is already running.");
            _loop = LoopAsync(cancellationToken);
            return _loop;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _stop.Token, cancellationToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                Pulse();
                await Task.Delay(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { _stop.Cancel(); }
    }

    private async Task TickAsync(Guid id, ScheduledProfile profile)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _stop.Token, profile.Cancellation.Token);
        CoordinatorResult? result = null;
        string? error = null;
        try { result = await profile.Coordinator.TickAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            lock (_sync)
            {
                if (_profiles.TryGetValue(id, out ScheduledProfile? current) &&
                    ReferenceEquals(profile, current))
                {
                    profile.LastResult = result;
                    profile.Error = error;
                    profile.NextDue = profile.ImmediateRequested
                        ? _clock.Elapsed
                        : NextDue(_clock.Elapsed, result?.Snapshot, profile.Policy);
                    profile.ImmediateRequested = false;
                    profile.InFlight = null;
                }
            }
        }
    }

    public (CoordinatorResult? Result, string? Error)? GetLast(Guid id)
    {
        lock (_sync)
        {
            return _profiles.TryGetValue(id, out ScheduledProfile? profile)
                ? (profile.LastResult, profile.Error)
                : null;
        }
    }

    internal static TimeSpan NextDue(TimeSpan now, RecoverySnapshot? snapshot,
        RecoveryPolicy policy)
    {
        if (snapshot is null) return now + policy.NormalPollInterval;
        TimeSpan interval = snapshot.State switch
        {
            RecoveryState.Starting or RecoveryState.Observing => policy.ObservationPollInterval,
            RecoveryState.AwaitingIntervention => policy.LockoutDiscoveryInterval,
            _ => policy.NormalPollInterval
        };
        if (snapshot.DetectionUnavailable || snapshot.Paused || !snapshot.Enabled)
            interval = policy.NormalPollInterval;
        else
        {
            if (snapshot.RetryDeadline is null && snapshot.AbsenceStartedAt is { } absentAt)
                interval = Min(interval, absentAt + policy.AbsenceConfirmationDelay - now);
            if (snapshot.RetryDeadline is { } retryAt)
                interval = Min(interval, retryAt - now);
            if (snapshot.AppearanceDeadline is { } appearanceAt)
                interval = Min(interval, appearanceAt - now);
            if (snapshot.ObservationStartedAt is { } observedAt)
                interval = Min(interval, observedAt + policy.ObservationPeriod - now);
        }
        return now + (interval < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100) : interval);
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first < second ? first : second;

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        Guid[] ids;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
            ids = _profiles.Keys.ToArray();
        }
        _stop.Cancel();
        if (loop is not null) await loop.ConfigureAwait(false);
        await Task.WhenAll(ids.Select(RemoveAsync)).ConfigureAwait(false);
        _stop.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RecoveryScheduler));
    }

    private sealed class ScheduledProfile(
        ProfileCoordinator coordinator, RecoveryPolicy policy, TimeSpan nextDue)
    {
        public ProfileCoordinator Coordinator { get; } = coordinator;
        public RecoveryPolicy Policy { get; } = policy;
        public CancellationTokenSource Cancellation { get; } = new();
        public TimeSpan NextDue { get; set; } = nextDue;
        public bool ImmediateRequested { get; set; }
        public Task? InFlight { get; set; }
        public CoordinatorResult? LastResult { get; set; }
        public string? Error { get; set; }
    }
}
