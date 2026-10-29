using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;

namespace Relight.Core.Tests;

public sealed class StorageScanEstimateTests
{
    private sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed + WallOffset;
        public void Advance(double seconds) => _elapsed += TimeSpan.FromSeconds(seconds);
    }
    private static StorageScanSnapshot Prior(int count = 10000, bool limited = false, int skipped = 0) =>
        new(1, @"C:\scan", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1),
            !limited, count, count * 4 / 5, skipped, 0, limited, [], []);

    [Fact]
    public void EntryWorkloadIncludesDirectoriesAndWaitTimeDoesNotInflateEta()
    {
        var clock = new Clock(); var estimator = new StorageScanEstimator(clock);
        estimator.Begin(@"C:\scan", 100000, Prior());
        clock.Advance(120); Assert.Equal(StorageEstimateState.Waiting, estimator.Estimate().State);
        estimator.Start(); clock.Advance(10); estimator.Observe(500);
        var estimate = estimator.Estimate();
        Assert.Equal(10000, estimate.PriorEntries); Assert.Equal(8000, estimate.PriorFiles);
        Assert.Equal(9500, estimate.RemainingEntries); Assert.Equal(TimeSpan.FromSeconds(190), estimate.RemainingTime);
        Assert.Equal(TimeSpan.FromSeconds(10), estimate.Elapsed);
        clock.WallOffset = TimeSpan.FromDays(100);
        Assert.Equal(estimate.RemainingTime, estimator.Estimate().RemainingTime);
    }

    [Fact]
    public void DifferentOrMissingRootHasUnknownRemainingWorkButCanonicalCaseMatches()
    {
        var estimator = new StorageScanEstimator(new Clock());
        estimator.Begin(@"C:\other", 100000, Prior()); estimator.Start();
        Assert.Equal(StorageEstimateState.NoReference, estimator.Estimate().State);
        Assert.Null(estimator.Estimate().PriorEntries);
        estimator.Begin(@"c:\SCAN\", 100000, Prior()); estimator.Start();
        Assert.Equal(10000, estimator.Estimate().PriorEntries);
        estimator.Begin(@"C:\scan", 100000, null); estimator.Start();
        Assert.Null(estimator.Estimate().RemainingTime);
    }

    [Fact]
    public void LimitedPriorCountsAreOnlyLowerBoundsEvenAfterTheyAreExceeded()
    {
        var clock = new Clock(); var estimator = new StorageScanEstimator(clock);
        estimator.Begin(@"C:\scan", 1000000, Prior(100000, limited: true));
        estimator.Start(); clock.Advance(10); estimator.Observe(200000);
        var estimate = estimator.Estimate(); Assert.Equal(100000, estimate.PriorEntries);
        Assert.Equal(StorageEstimateState.PartialReference, estimate.State);
        Assert.Null(estimate.RemainingEntries); Assert.Null(estimate.RemainingTime);
    }

    [Fact]
    public void FinishedTraversalWithGapsEstimatesObservedWorkWithoutClaimingCompleteCoverage()
    {
        var clock = new Clock(); var estimator = new StorageScanEstimator(clock);
        estimator.Begin(@"C:\scan", 100000, Prior(skipped: 5)); estimator.Start();
        clock.Advance(10); estimator.Observe(500);
        var estimate = estimator.Estimate(); Assert.False(estimate.PriorComplete);
        Assert.True(estimate.PriorTraversalFinished); Assert.Equal(StorageEstimateState.Estimated, estimate.State);
    }

    [Fact]
    public void LowerCurrentBudgetEstimatesTimeToLimitNotWholeCoverage()
    {
        var clock = new Clock(); var estimator = new StorageScanEstimator(clock);
        estimator.Begin(@"C:\scan", 600, Prior(1000)); estimator.Start();
        clock.Advance(10); estimator.Observe(500);
        var estimate = estimator.Estimate(); Assert.True(estimate.ToEntryLimit);
        Assert.Equal(100, estimate.RemainingEntries); Assert.Equal(TimeSpan.FromSeconds(2), estimate.RemainingTime);
        estimator.Observe(600); Assert.Equal(StorageEstimateState.ReferenceExceeded, estimator.Estimate().State);
        Assert.Null(estimator.Estimate().RemainingTime);
    }

    [Fact]
    public void GrowingTreeWarmupStallsAndSavingNeverInventAZeroEta()
    {
        var clock = new Clock(); var estimator = new StorageScanEstimator(clock);
        estimator.Begin(@"C:\scan", 100000, Prior(1000)); estimator.Start();
        estimator.Observe(250); Assert.Equal(StorageEstimateState.Measuring, estimator.Estimate().State);
        clock.Advance(2); Assert.Equal(StorageEstimateState.Estimated, estimator.Estimate().State);
        clock.Advance(28); Assert.Equal(StorageEstimateState.Stalled, estimator.Estimate().State);
        Assert.Null(estimator.Estimate().RemainingTime);
        estimator.Observe(500); Assert.Equal(StorageEstimateState.Estimated, estimator.Estimate().State);
        estimator.Observe(1000); Assert.Equal(StorageEstimateState.ReferenceExceeded, estimator.Estimate().State);
        estimator.Saving(); Assert.Equal(StorageEstimateState.Saving, estimator.Estimate().State);
        Assert.Null(estimator.Estimate().RemainingTime);
    }

    [Fact]
    public async Task LoadedCountsAreVisibleDuringQueuedScanAndSurviveCancellation()
    {
        string data = Path.Combine(Path.GetTempPath(), "relight-eta-" + Guid.NewGuid().ToString("N"));
        using var gate = new SemaphoreSlim(0, 1);
        var model = new StorageScanViewModel(data, gate, clock: new Clock());
        try
        {
            new StorageScanStore(data).Save(Prior()); await model.LoadAsync();
            var scan = model.ScanAsync();
            Assert.Contains("file paths measured", model.PriorWorkSummary);
            Assert.Contains("Complete prior coverage", model.PriorWorkSummary);
            Assert.Contains("timing has not started", model.WorkEstimateText);
            model.Cancel(); await scan;
            Assert.Equal(10000, new StorageScanStore(data).Load()!.EntriesVisited);
            Assert.False(model.IsScanning);
        }
        finally { model.Close(); if (Directory.Exists(data)) Directory.Delete(data, true); }
    }
}
