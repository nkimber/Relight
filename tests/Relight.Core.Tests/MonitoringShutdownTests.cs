using Relight.Services;

namespace Relight.Core.Tests;

public sealed class MonitoringShutdownTests
{
    [Fact]
    public async Task Exit_waits_for_cleanup_and_releases_its_deadline()
    {
        var clock = new ManualTimeProvider();
        var monitoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task drain = MonitoringShutdown.DrainAsync(monitoring.Task, clock);
        Assert.False(drain.IsCompleted);
        monitoring.SetResult();
        await drain;
        Assert.True(clock.Timer!.Disposed);
    }

    [Fact]
    public async Task Stuck_adapter_cannot_hold_exit_past_the_cleanup_deadline()
    {
        var clock = new ManualTimeProvider();
        var monitoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task drain = MonitoringShutdown.DrainAsync(monitoring.Task, clock);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.DueTime);
        clock.Timer!.Fire();
        await Assert.ThrowsAsync<TimeoutException>(() => drain);
        Assert.False(monitoring.Task.IsCompleted);
        Assert.True(clock.Timer.Disposed);
    }

    [Fact]
    public async Task Cleanup_failure_is_observed_and_shell_only_exit_completes()
    {
        await MonitoringShutdown.DrainAsync(null);
        await Assert.ThrowsAsync<IOException>(() => MonitoringShutdown.DrainAsync(
            Task.FromException(new IOException("controlled cleanup failure"))));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public ManualTimer? Timer { get; private set; }
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return Timer = new ManualTimer(callback, state);
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
