using Relight.Core;

namespace Relight.Engine;

/// <summary>
/// One shared timer scans monotonic per-profile deadlines. Due ticks run
/// independently, so a slow adapter cannot block other profiles or the timer.
/// Pulse is also a deterministic test/harness entry point.
/// </summary>
public sealed class RecoveryScheduler : IAsyncDisposable
{
    private static readonly TimeSpan MonitoringGapThreshold = TimeSpan.FromSeconds(5);
    private readonly IMonotonicClock _clock;
    private readonly Dictionary<Guid, ScheduledProfile> _profiles = new();
    private readonly Dictionary<Guid, PassiveProfile> _passive = new();
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
            if (_passive.ContainsKey(id) || !_profiles.TryAdd(id,
                    new(coordinator, policy, _clock.Elapsed)))
                throw new InvalidOperationException("Profile is already scheduled.");
        }
    }

    public void AddPassive(Guid id, IProcessDiscovery discovery, TimeSpan interval,
        Detection? initialDetection = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Profile ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(discovery);
        if (interval < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_sync)
        {
            ThrowIfDisposed();
            var profile = new PassiveProfile(discovery, interval,
                _clock.Elapsed + (initialDetection is null ? TimeSpan.Zero : interval))
            {
                LastDetection = initialDetection
            };
            if (_profiles.ContainsKey(id) || !_passive.TryAdd(id, profile))
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
                if (profile.InFlight is not null || profile.GapPending ||
                    now < profile.NextDue) continue;
                profile.ImmediateRequested = false;
                // Task.Run prevents a synchronously blocked adapter from holding
                // the scheduler's scan lock or delaying another due profile.
                profile.InFlight = Task.Run(() => TickAsync(id, profile), CancellationToken.None);
                started.Add(id, profile.InFlight);
            }
            foreach ((Guid id, PassiveProfile profile) in _passive)
            {
                if (profile.InFlight is not null || now < profile.NextDue) continue;
                profile.ImmediateRequested = false;
                profile.InFlight = Task.Run(() => PassiveTickAsync(id, profile), CancellationToken.None);
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
            if (_profiles.TryGetValue(id, out ScheduledProfile? profile))
            {
                if (profile.InFlight is null) profile.NextDue = _clock.Elapsed;
                else profile.ImmediateRequested = true;
            }
            else if (_passive.TryGetValue(id, out PassiveProfile? passive))
            {
                if (passive.InFlight is null) passive.NextDue = _clock.Elapsed;
                else passive.ImmediateRequested = true;
            }
        }
    }

    public void RequestImmediatePassive()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            TimeSpan now = _clock.Elapsed;
            foreach (PassiveProfile profile in _passive.Values)
            {
                if (profile.InFlight is null) profile.NextDue = now;
                else profile.ImmediateRequested = true;
            }
        }
    }

    public void UpdatePolicy(Guid id, RecoveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_profiles.TryGetValue(id, out ScheduledProfile? profile))
                throw new InvalidOperationException("Profile is not scheduled for recovery.");
            profile.Policy = policy;
            if (profile.InFlight is null) profile.NextDue = _clock.Elapsed;
            else profile.ImmediateRequested = true;
        }
    }

    public void UpdatePassiveInterval(Guid id, TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_passive.TryGetValue(id, out PassiveProfile? profile)) return;
            profile.Interval = interval;
            if (profile.InFlight is null) profile.NextDue = _clock.Elapsed;
            else profile.ImmediateRequested = true;
        }
    }

    public async Task RemoveAsync(Guid id)
    {
        ScheduledProfile? profile;
        PassiveProfile? passive;
        lock (_sync)
        {
            _profiles.Remove(id, out profile);
            _passive.Remove(id, out passive);
        }
        if (profile is not null)
        {
            profile.Cancellation.Cancel();
            if (profile.InFlight is { } running) await running.ConfigureAwait(false);
            if (profile.GapTask is { } gap) await gap.ConfigureAwait(false);
            profile.Coordinator.Dispose();
            profile.Cancellation.Dispose();
        }
        if (passive is not null)
        {
            passive.Cancellation.Cancel();
            if (passive.InFlight is { } running) await running.ConfigureAwait(false);
            passive.Cancellation.Dispose();
        }
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
        TimeSpan previousPulse = _clock.Elapsed;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                PulseAfterMonitoringGap(previousPulse);
                previousPulse = _clock.Elapsed;
                await Task.Delay(TimeSpan.FromSeconds(1), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { _stop.Cancel(); }
    }

    internal IReadOnlyDictionary<Guid, Task> PulseAfterMonitoringGap(
        TimeSpan previousPulse)
    {
        if (_clock.Elapsed - previousPulse > MonitoringGapThreshold)
            BeginMonitoringGapReconciliation();
        return Pulse();
    }

    internal Task? GetGapReconciliation(Guid id)
    {
        lock (_sync)
            return _profiles.GetValueOrDefault(id)?.GapTask;
    }

    private void BeginMonitoringGapReconciliation()
    {
        lock (_sync)
        {
            if (_disposed || _stop.IsCancellationRequested) return;
            foreach ((Guid id, ScheduledProfile profile) in _profiles)
            {
                if (profile.GapPending) continue;
                profile.GapPending = true;
                profile.Coordinator.CancelPendingLaunchForMonitoringInterruption();
                profile.GapTask = Task.Run(() => ReconcileGapAsync(id, profile));
            }
            TimeSpan now = _clock.Elapsed;
            foreach (PassiveProfile profile in _passive.Values)
            {
                if (profile.InFlight is null) profile.NextDue = now;
                else profile.ImmediateRequested = true;
            }
        }
    }

    private async Task ReconcileGapAsync(Guid id, ScheduledProfile profile)
    {
        string? error = null;
        try
        {
            await profile.Coordinator.MarkMonitoringInterruptedAsync(profile.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (profile.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            lock (_sync)
            {
                if (_profiles.TryGetValue(id, out ScheduledProfile? current) &&
                    ReferenceEquals(profile, current))
                {
                    profile.Error = error;
                    profile.GapPending = error is not null;
                    if (error is null) profile.NextDue = _clock.Elapsed;
                }
            }
        }
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
                    if (profile.Coordinator.ReconciliationPending &&
                        (result?.Snapshot ?? profile.Coordinator.Snapshot) is
                            { Enabled: true, Paused: false })
                    {
                        TimeSpan reconciliationDue = _clock.Elapsed +
                            profile.Policy.ObservationPollInterval;
                        if (reconciliationDue < profile.NextDue)
                            profile.NextDue = reconciliationDue;
                    }
                    profile.ImmediateRequested = false;
                    profile.InFlight = null;
                }
            }
        }
    }

    private async Task PassiveTickAsync(Guid id, PassiveProfile profile)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _stop.Token, profile.Cancellation.Token);
        Detection? result = null;
        try { result = await profile.Discovery.DetectAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error) { result = Detection.Unavailable(error.Message); }
        finally
        {
            lock (_sync)
            {
                if (_passive.TryGetValue(id, out PassiveProfile? current) &&
                    ReferenceEquals(profile, current))
                {
                    if (result is not null) profile.LastDetection = result;
                    profile.NextDue = profile.ImmediateRequested
                        ? _clock.Elapsed : _clock.Elapsed + profile.Interval;
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

    public Detection? GetPassiveLast(Guid id)
    {
        lock (_sync)
        {
            return _passive.TryGetValue(id, out PassiveProfile? profile)
                ? profile.LastDetection : null;
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
            ids = _profiles.Keys.Concat(_passive.Keys).ToArray();
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
        public RecoveryPolicy Policy { get; set; } = policy;
        public CancellationTokenSource Cancellation { get; } = new();
        public TimeSpan NextDue { get; set; } = nextDue;
        public bool ImmediateRequested { get; set; }
        public Task? InFlight { get; set; }
        public bool GapPending { get; set; }
        public Task? GapTask { get; set; }
        public CoordinatorResult? LastResult { get; set; }
        public string? Error { get; set; }
    }

    private sealed class PassiveProfile(
        IProcessDiscovery discovery, TimeSpan interval, TimeSpan nextDue)
    {
        public IProcessDiscovery Discovery { get; } = discovery;
        public TimeSpan Interval { get; set; } = interval;
        public CancellationTokenSource Cancellation { get; } = new();
        public TimeSpan NextDue { get; set; } = nextDue;
        public bool ImmediateRequested { get; set; }
        public Task? InFlight { get; set; }
        public Detection? LastDetection { get; set; }
    }
}
