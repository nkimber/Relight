using Relight.Core;
using Relight.Storage;

namespace Relight.Windows;

public sealed record AutomaticCleanupPulse(string Status, CleanupRun? Run = null);

/// <summary>Opt-in rules only. No catch-up loop; reserve at most one bounded run per pulse.</summary>
public sealed class AutomaticCleanupService : IDisposable
{
    private readonly CleanupStore _store;
    private readonly TimeProvider _clock;
    private readonly Func<string, long?> _available;
    private readonly Func<ReviewedCleanupService> _createCleanup;
    private readonly Dictionary<Guid, long> _firstSeen = [];
    private readonly SemaphoreSlim _pulseGate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _active;
    private bool _stopped;
    private FileStream? _workerLease;
    public void Stop() { lock (_sync) { _stopped = true; _active?.Cancel(); } }
    public void AllowApprovedRules() { lock (_sync) _stopped = false; }
    public void Dispose() { Stop(); lock (_sync) { _workerLease?.Dispose(); _workerLease = null; } }

    public AutomaticCleanupService(string dataDirectory, TimeProvider? clock = null,
        Func<string, long?>? available = null, Func<ReviewedCleanupService>? createCleanup = null)
    {
        _clock = clock ?? TimeProvider.System;
        _store = new(dataDirectory, _clock);
        _available = available ?? (root => new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace);
        _createCleanup = createCleanup ?? (() => new ReviewedCleanupService(dataDirectory, clock: _clock));
    }

    public async Task<AutomaticCleanupPulse> PulseAsync(SemaphoreSlim workGate, CancellationToken token = default)
    {
        if (!await _pulseGate.WaitAsync(0, token).ConfigureAwait(false)) return new("Automatic cleanup is already checking.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (_sync)
            {
                if (_stopped) return new("Automatic cleanup stopped in this instance. A new explicit approval is required to resume.");
                _active = cancellation;
            }
            token = cancellation.Token;
            var book = _store.LoadRules();
            if (!book.AutomaticEnabled)
            {
                _firstSeen.Clear(); lock (_sync) { _workerLease?.Dispose(); _workerLease = null; }
                return new("Automatic cleanup is off.");
            }
            lock (_sync)
            {
                if (_stopped) return new("Automatic cleanup stopped in this instance.");
                if (_workerLease is null)
                {
                    try { _workerLease = _store.AcquireAutomaticWorkerLease(); }
                    catch (IOException) { return new("Automatic cleanup is owned by another Relight instance or its worker lease is unavailable."); }
                    _firstSeen.Clear(); // A new owner always waits the complete monotonic interval.
                }
            }
            foreach (var old in _firstSeen.Keys.Where(id => !book.AutomaticApprovals.Any(a => a.Id == id)).ToArray()) _firstSeen.Remove(old);
            foreach (var approval in book.AutomaticApprovals)
            {
                token.ThrowIfCancellationRequested();
                if (!_firstSeen.TryGetValue(approval.Id, out long seen))
                { _firstSeen[approval.Id] = _clock.GetTimestamp(); continue; }
                if (_clock.GetElapsedTime(seen) < TimeSpan.FromHours(approval.IntervalHours) || approval.NextEligibleUtc > _clock.GetUtcNow()) continue;
                var rule = book.Rules.Single(r => r.Id == approval.RuleId);
                long? available = _available(rule.Root);
                if (available is null || available < 0) return new("Automatic cleanup suspended: available disk space is unknown.");
                if (available >= approval.TriggerBelowBytes) continue;
                if (!await workGate.WaitAsync(0, token).ConfigureAwait(false)) return new("Automatic cleanup deferred while another storage operation runs.");
                try
                {
                    rule = _store.ReserveAutomatic(approval.Id);
                    if (rule is null) continue;
                    _firstSeen[approval.Id] = _clock.GetTimestamp();
                    var service = _createCleanup();
                    var started = _clock.GetUtcNow();
                    CleanupPreview preview;
                    try { preview = await service.PreviewAsync(rule, token).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { return RecordNoDispatch(rule, started, $"Automatic cleanup preflight blocked: {error.Message}"); }
                    if (!preview.Complete || preview.Candidates.Length == 0)
                        return RecordNoDispatch(rule, started, "Automatic cleanup checked the rule; incomplete coverage or no eligible files. This interval is consumed.");
                    var run = await service.ExecuteReviewedAsync(preview.Id, preview.Candidates.Select(f => f.Path).ToArray(), token,
                        () => _store.AcquireAutomaticAuthorization(approval.Id, approval.RuleFingerprint)).ConfigureAwait(false);
                    return new($"Automatic cleanup run {run.Id}: {run.Items.Count(i => i.Outcome == "Deleted permanently")} file(s) deleted. {run.Problem}", run);
                }
                finally { workGate.Release(); }
            }
            return new($"Automatic cleanup armed for {book.AutomaticApprovals.Length} approved rule(s). Waiting for low space and interval; restarting Relight waits a full interval again.");
        }
        finally
        {
            lock (_sync)
            {
                _active = null;
                if (_stopped) { _workerLease?.Dispose(); _workerLease = null; }
            }
            _pulseGate.Release();
        }
    }

    private AutomaticCleanupPulse RecordNoDispatch(CleanupRule rule, DateTimeOffset started, string reason)
    {
        using var lease = _store.AcquireRunLease();
        var run = new CleanupRun(Guid.NewGuid(), rule.Id, started, _clock.GetUtcNow(), [], null, null, reason) { Origin = "Automatic" };
        _store.SaveRun(run);
        return new(reason, run);
    }
}
