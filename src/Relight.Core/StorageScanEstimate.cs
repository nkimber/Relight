namespace Relight.Core;

public enum StorageEstimateState { NoReference, PartialReference, Waiting, Measuring, Estimated, ReferenceExceeded, Stalled, Saving }

public sealed record StorageWorkEstimate(StorageEstimateState State, int? PriorEntries, int? PriorFiles,
    DateTimeOffset? PriorFinishedUtc, bool PriorComplete, bool PriorTraversalFinished, int? RemainingEntries,
    TimeSpan Elapsed, TimeSpan? RemainingTime, bool ToEntryLimit);

/// <summary>Observed prior workload is a reference, never a guarantee about the current tree.</summary>
public sealed class StorageScanEstimator(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private StorageScanSnapshot? _prior;
    private int _limit, _inspected;
    private long _started, _lastProgress;
    private bool _running, _saving;

    public void Begin(string root, int limit, StorageScanSnapshot? prior)
    {
        if (limit is < 1 or > StorageScanSnapshot.MaximumEntryLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        _prior = prior is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(prior.Root)), StringComparison.OrdinalIgnoreCase) ? prior : null;
        _limit = limit; _inspected = 0; _running = false; _saving = false;
    }
    public void Start() { _started = _lastProgress = _clock.GetTimestamp(); _running = true; }
    public void Observe(int inspected)
    {
        if (inspected <= _inspected) return;
        _inspected = inspected; _lastProgress = _clock.GetTimestamp();
    }
    public void Saving() => _saving = true;

    public StorageWorkEstimate Estimate()
    {
        TimeSpan elapsed = _running ? _clock.GetElapsedTime(_started) : TimeSpan.Zero;
        bool complete = _prior?.CoverageComplete == true;
        bool traversalFinished = _prior?.TraversalComplete == true && !_prior.EntryLimitReached;
        bool toLimit = traversalFinished && _limit < _prior!.EntriesVisited;
        StorageWorkEstimate Result(StorageEstimateState state, int? remaining = null, TimeSpan? time = null) =>
            new(state, _prior?.EntriesVisited, _prior?.FilesMeasured, _prior?.FinishedUtc, complete, traversalFinished, remaining, elapsed, time, toLimit);
        if (_saving) return Result(StorageEstimateState.Saving);
        if (!_running) return Result(StorageEstimateState.Waiting);
        if (_prior is null) return Result(StorageEstimateState.NoReference);
        if (!traversalFinished) return Result(StorageEstimateState.PartialReference);
        int target = Math.Min(_prior.EntriesVisited, _limit);
        if (_inspected >= target) return Result(StorageEstimateState.ReferenceExceeded);
        int remaining = target - _inspected;
        if (_clock.GetElapsedTime(_lastProgress) >= TimeSpan.FromSeconds(30)) return Result(StorageEstimateState.Stalled, remaining);
        if (_inspected < 250 || elapsed < TimeSpan.FromSeconds(2)) return Result(StorageEstimateState.Measuring, remaining);
        double seconds = remaining * elapsed.TotalSeconds / _inspected;
        if (!double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds / 2)
            return Result(StorageEstimateState.Measuring, remaining);
        return Result(StorageEstimateState.Estimated, remaining, TimeSpan.FromSeconds(seconds));
    }
}
