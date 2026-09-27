using Relight.Core;
using Relight.Services;
using Relight.Storage;

namespace Relight.Core.Tests;

public sealed class RecoveryNotificationTests
{
    [Fact]
    public void Live_tap_forwards_all_events_and_bounds_actionable_queue()
    {
        var recorder = new CountingRecorder();
        var tap = new LiveNotificationTap(recorder, capacity: 2);
        OperationalEvent first = Event(OperationalEventKind.LockoutEntered);
        OperationalEvent ordinary = Event(OperationalEventKind.StateChanged);
        OperationalEvent second = Event(OperationalEventKind.ObservationCompleted);
        OperationalEvent third = Event(OperationalEventKind.LockoutEntered);

        foreach (OperationalEvent entry in new[] { first, ordinary, second, third })
            Assert.True(tap.TryRecord(entry));

        Assert.Equal(4, recorder.Count);
        Assert.Equal([second, third], tap.Drain());
        Assert.Empty(tap.Drain());
    }

    [Fact]
    public void Lockout_notice_is_once_per_episode_and_respects_preference()
    {
        var planner = new RecoveryNotificationPlanner();
        ProfileConfiguration profile = Profile();
        Guid episode = Guid.NewGuid();
        OperationalEvent locked = Event(OperationalEventKind.LockoutEntered,
            profile.Id, episode) with { AttemptNumber = 3, AttemptLimit = 3 };

        RecoveryNotification notice = Assert.IsType<RecoveryNotification>(
            planner.Plan(locked, profile));
        Assert.True(notice.Warning);
        Assert.Contains("3 of 3", notice.Message);
        Assert.Null(planner.Plan(locked, profile));
        Assert.NotNull(planner.Plan(locked with { EpisodeId = Guid.NewGuid() }, profile));
        Assert.Null(planner.Plan(locked with { EpisodeId = Guid.NewGuid() },
            profile with { NotifyOnLockout = false }));
    }

    [Fact]
    public void Recovery_notice_requires_stable_automatic_observation()
    {
        var planner = new RecoveryNotificationPlanner();
        ProfileConfiguration profile = Profile();
        OperationalEvent completed = Event(OperationalEventKind.ObservationCompleted,
            profile.Id, Guid.NewGuid());

        Assert.Null(planner.Plan(completed with
            { Origin = ObservationOrigin.ExternalStart }, profile));
        Assert.Null(planner.Plan(completed with
            { Origin = ObservationOrigin.AutomaticLaunch },
            profile with { NotifyOnRecovery = false }));
        Assert.False(Assert.IsType<RecoveryNotification>(planner.Plan(completed with
            { Origin = ObservationOrigin.AutomaticLaunch }, profile)).Warning);
        Assert.Null(planner.Plan(completed with
            { Origin = ObservationOrigin.AutomaticLaunch }, profile));
    }

    private static ProfileConfiguration Profile() => new(Guid.NewGuid(), "Disposable app",
        true, new(TargetKind.Executable, @"C:\Disposable\app.exe", []),
        RecoveryPolicy.Default);

    private static OperationalEvent Event(OperationalEventKind kind,
        Guid? profile = null, Guid? episode = null) =>
        new(DateTimeOffset.UtcNow, Guid.NewGuid(), EventSeverity.Information, kind,
            ProfileId: profile, EpisodeId: episode);

    private sealed class CountingRecorder : IEventRecorder
    {
        public int Count { get; private set; }
        public bool TryRecord(OperationalEvent entry)
        {
            Count++;
            return true;
        }
    }
}
