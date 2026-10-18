using Relight.Core;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class DiskSpaceNotificationTests
{
    private const long GiB = 1_073_741_824;
    private sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero) + _elapsed + WallOffset;
        public void Advance(double minutes) => _elapsed += TimeSpan.FromMinutes(minutes);
    }

    [Fact]
    public void DefaultRemindersStayOffAndBoundaryProducesOneReminderWithoutCatchUp()
    {
        var clock = new Clock();
        var monitor = new DiskSpaceMonitor(_ => (19 * GiB, 100 * GiB), clock);
        var settings = new DiskSpaceSettings();
        Assert.True(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(10080); Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        settings = settings with { ReminderIntervalMinutes = 15 };
        var reminder = Assert.Single(monitor.Sample(settings));
        Assert.True(reminder.Notify); Assert.True(reminder.IsReminder);
        Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(14); Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(1); Assert.True(Assert.Single(monitor.Sample(settings)).IsReminder);
    }

    [Fact]
    public void EscalationIsImmediateAndRecoveryRearmsAnEntryInsteadOfReminder()
    {
        var clock = new Clock(); long free = 19 * GiB;
        var monitor = new DiskSpaceMonitor(_ => (free, 100 * GiB), clock);
        var settings = new DiskSpaceSettings { ReminderIntervalMinutes = 60 };
        monitor.Sample(settings);
        clock.Advance(1); free = 4 * GiB;
        var critical = Assert.Single(monitor.Sample(settings)); Assert.True(critical.Notify); Assert.False(critical.IsReminder);
        clock.Advance(59); Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        free = 30 * GiB; Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        free = 19 * GiB; var entry = Assert.Single(monitor.Sample(settings)); Assert.True(entry.Notify); Assert.False(entry.IsReminder);
    }

    [Fact]
    public void SnoozeSuppressesCriticalAndResumesFromFreshReadingWithRemindersOff()
    {
        var clock = new Clock(); long free = 19 * GiB;
        var monitor = new DiskSpaceMonitor(_ => (free, 100 * GiB), clock);
        var settings = new DiskSpaceSettings(); monitor.Sample(settings);
        monitor.SnoozeNotifications(TimeSpan.FromMinutes(60)); free = 4 * GiB;
        var quiet = Assert.Single(monitor.Sample(settings)); Assert.Equal(DiskSpaceLevel.Critical, quiet.Level); Assert.False(quiet.Notify);
        clock.Advance(59); Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(1); Assert.Equal(TimeSpan.Zero, monitor.SnoozeRemaining);
        Assert.True(Assert.Single(monitor.Sample(settings)).Notify);
        Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        monitor.SnoozeNotifications(TimeSpan.FromMinutes(60)); monitor.ResumeNotifications();
        Assert.True(Assert.Single(monitor.Sample(settings)).Notify); // Even if no sample happened during snooze.
    }

    [Fact]
    public void HealthyOrUnknownAtSnoozeExpiryNeverProducesLowSpaceNotice()
    {
        var clock = new Clock(); long free = 4 * GiB;
        var monitor = new DiskSpaceMonitor(_ => (free, 100 * GiB), clock);
        var settings = new DiskSpaceSettings(); monitor.Sample(settings);
        monitor.SnoozeNotifications(TimeSpan.FromMinutes(5)); clock.Advance(5);
        free = -1; Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        free = 30 * GiB; Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        free = 4 * GiB; Assert.True(Assert.Single(monitor.Sample(settings)).Notify);
    }

    [Fact]
    public void MutingAndUnknownReadingsDoNotConsumePendingEntryOrRearmOldEpisode()
    {
        var clock = new Clock(); bool fail = false;
        var monitor = new DiskSpaceMonitor(_ => fail ? throw new IOException("test gap") : (4 * GiB, 100 * GiB), clock);
        var settings = new DiskSpaceSettings { ReminderIntervalMinutes = 15 };
        Assert.False(Assert.Single(monitor.Sample(settings with { NotificationsEnabled = false })).Notify);
        Assert.True(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(15); fail = true; Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        fail = false; Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(15); Assert.True(Assert.Single(monitor.Sample(settings)).IsReminder);
        clock.Advance(15); monitor.Sample(settings with { NotificationsEnabled = false });
        Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
    }

    [Fact]
    public void WallClockJumpsCannotExpireSnoozeOrAccelerateReminder()
    {
        var clock = new Clock(); var monitor = new DiskSpaceMonitor(_ => (4 * GiB, 100 * GiB), clock);
        var settings = new DiskSpaceSettings { ReminderIntervalMinutes = 15 }; monitor.Sample(settings);
        clock.WallOffset = TimeSpan.FromDays(100); Assert.False(Assert.Single(monitor.Sample(settings)).Notify);
        clock.Advance(15); Assert.True(Assert.Single(monitor.Sample(settings)).IsReminder);
        monitor.SnoozeNotifications(TimeSpan.FromMinutes(60)); clock.WallOffset = TimeSpan.FromDays(-100);
        Assert.Equal(TimeSpan.FromMinutes(60), monitor.SnoozeRemaining);
        clock.Advance(60); Assert.True(Assert.Single(monitor.Sample(settings)).Notify);
    }

    [Fact]
    public void DrivesAreIndependentAndRemovingDisablingOrRestartingResetsEvaluation()
    {
        var clock = new Clock(); var monitor = new DiskSpaceMonitor(_ => (4 * GiB, 100 * GiB), clock);
        var settings = new DiskSpaceSettings { Drives = [new("C:\\"), new("D:\\")], ReminderIntervalMinutes = 15 };
        Assert.All(monitor.Sample(settings), r => Assert.True(r.Notify));
        clock.Advance(15); Assert.All(monitor.Sample(settings), r => Assert.True(r.IsReminder));
        monitor.Sample(settings with { Drives = [new("C:\\")] });
        var added = monitor.Sample(settings); Assert.False(added[0].Notify); Assert.True(added[1].Notify);
        monitor.SnoozeNotifications(TimeSpan.FromMinutes(60));
        Assert.Empty(monitor.Sample(settings with { Enabled = false }));
        Assert.All(monitor.Sample(settings), r => Assert.False(r.Notify));
        var restarted = new DiskSpaceMonitor(_ => (4 * GiB, 100 * GiB), clock);
        Assert.Equal(TimeSpan.Zero, restarted.SnoozeRemaining); Assert.All(restarted.Sample(settings), r => Assert.True(r.Notify));
    }

    [Theory]
    [InlineData(-1)] [InlineData(1)] [InlineData(14)] [InlineData(10081)]
    public void InvalidReminderIntervalsAreRejected(int minutes) =>
        Assert.Throws<ArgumentException>(() => new DiskSpaceSettings { ReminderIntervalMinutes = minutes }.Validate());

    [Fact]
    public void InvalidSnoozeDoesNotReplaceActiveSnooze()
    {
        var monitor = new DiskSpaceMonitor(clock: new Clock()); monitor.SnoozeNotifications(TimeSpan.FromMinutes(60));
        Assert.Throws<ArgumentException>(() => monitor.SnoozeNotifications(TimeSpan.FromMinutes(4)));
        Assert.Throws<ArgumentException>(() => monitor.SnoozeNotifications(TimeSpan.FromMinutes(1441)));
        Assert.True(monitor.SnoozeRemaining > TimeSpan.FromMinutes(59));
    }
}
