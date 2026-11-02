using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Relight.Core;
using Relight.Engine;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class PackagedProcessProbeTests
{
    [Fact]
    public async Task Cancellation_before_dispatch_does_not_enumerate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool enumerated = false;
        var probe = CreateProbe(() => { enumerated = true; return []; });

        Task<PackagedProcessInspection> inspection = probe.InspectAsync(cancellation.Token);
        await ObserveCompletion(inspection);

        Assert.True(inspection.IsCanceled);
        Assert.False(enumerated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_enumeration_cancels_without_throwing_from_worker(
        bool hasCandidates)
    {
        using var cancellation = new CancellationTokenSource();
        using var first = new TrackedProcess();
        using var second = new TrackedProcess();
        int workerThread = 0;
        int cancellationThrows = 0;
        var probe = CreateProbe(() =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            cancellation.Cancel();
            return hasCandidates ? [first, second] : [];
        });
        void OnException(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == workerThread &&
                e.Exception is OperationCanceledException canceled &&
                canceled.CancellationToken == cancellation.Token)
                Interlocked.Increment(ref cancellationThrows);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnException;
        Task<PackagedProcessInspection> inspection;
        try
        {
            inspection = probe.InspectAsync(cancellation.Token);
            await ObserveCompletion(inspection);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }

        Assert.True(inspection.IsCanceled); // Never a successful empty/partial discovery.
        Assert.Equal(0, cancellationThrows);
        if (hasCandidates)
        {
            Assert.True(first.WasDisposed);
            Assert.True(second.WasDisposed);
        }
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspection);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Uncanceled_empty_enumeration_remains_a_successful_inspection()
    {
        PackagedProcessInspection inspection = await CreateProbe(() => []).InspectAsync();

        Assert.Empty(inspection.Candidates);
        Assert.Null(inspection.Problem);
    }

    [Fact]
    public async Task Enumeration_failure_remains_unavailable_rather_than_absent()
    {
        PackagedProcessInspection inspection = await CreateProbe(() =>
            throw new Win32Exception(5)).InspectAsync();

        Assert.NotNull(inspection.Problem);
        Assert.Equal(DetectionFailureKind.PermissionDenied, inspection.FailureKind);
        Assert.Equal(5, inspection.NativeErrorCode);
    }

    [Fact]
    public async Task Scheduler_shutdown_drains_inflight_probe_without_publishing_absence()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probe = CreateProbe(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Probe test was not released.");
            return [];
        });
        await using var scheduler = new RecoveryScheduler(new FakeClock());
        Guid profile = Guid.NewGuid();
        var discovery = new ProbeDiscovery(probe);
        scheduler.AddPassive(profile, discovery, TimeSpan.FromSeconds(1));
        Task tick = Assert.Single(scheduler.Pulse()).Value;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Task shutdown = scheduler.DisposeAsync().AsTask();
            release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            await tick;
            Assert.True(tick.IsCompletedSuccessfully);
            Assert.False(discovery.PublishedAbsence);
        }
        finally { release.Set(); }
    }

    private static PackagedProcessProbe CreateProbe(Func<Process[]> getProcesses) =>
        new("Relight.TestPackage", "Relight.TestTarget.exe", getProcesses);

    // Observe completion without awaiting the canceled task, so first-chance
    // tracking measures the worker rather than the test's expected await throw.
    private static Task ObserveCompletion(Task task) => task.ContinueWith(_ => { },
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class TrackedProcess : Process
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ProbeDiscovery(PackagedProcessProbe probe) : IProcessDiscovery
    {
        public bool PublishedAbsence { get; private set; }

        public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
        {
            await probe.InspectAsync(cancellationToken);
            PublishedAbsence = true;
            return Detection.Absent();
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Elapsed => TimeSpan.Zero;
    }
}
