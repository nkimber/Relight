using Relight.Core;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class ProfileTimingPresentationTests
{
    [Fact]
    public void Retry_and_appearance_countdowns_describe_checks_not_promised_launches()
    {
        RecoverySnapshot initial = new RecoveryMachine(RecoveryPolicy.Default).Snapshot;
        HostedProfileStatus retry = Profile(initial with
        {
            State = RecoveryState.RetryWaiting,
            RetryDeadline = TimeSpan.FromSeconds(10)
        });
        Assert.Contains("Retry eligible in 5s",
            ProfileTimingPresentation.FromProfile(retry, TimeSpan.FromSeconds(5)).NextAction);
        Assert.Contains("verifying target identity",
            ProfileTimingPresentation.FromProfile(retry, TimeSpan.FromSeconds(10)).NextAction);

        HostedProfileStatus starting = Profile(initial with
        {
            State = RecoveryState.Starting,
            AppearanceDeadline = TimeSpan.FromSeconds(20)
        });
        Assert.Contains("Appearance timeout in 1s",
            ProfileTimingPresentation.FromProfile(starting, TimeSpan.FromSeconds(19.1)).NextAction);
        Assert.Contains("awaiting a confirming check",
            ProfileTimingPresentation.FromProfile(starting, TimeSpan.FromSeconds(20)).NextAction);
    }

    [Fact]
    public void Observation_timer_marks_a_monitoring_gap_unverified()
    {
        RecoverySnapshot initial = new RecoveryMachine(RecoveryPolicy.Default).Snapshot;
        HostedProfileStatus observing = Profile(initial with
        {
            State = RecoveryState.Observing,
            ObservationStartedAt = TimeSpan.Zero,
            LastVerifiedAt = TimeSpan.FromSeconds(59),
            TargetIdentity = "session|target|1|2"
        });
        ProfileTimingPresentation continuous = ProfileTimingPresentation.FromProfile(
            observing, TimeSpan.FromMinutes(1));
        Assert.Contains("9m 0s", continuous.NextAction);
        Assert.Contains("1s ago", continuous.LastSeen);
        Assert.Contains("Matching current-session", continuous.Instance);

        HostedProfileStatus stale = observing with
        {
            Recovery = observing.Recovery! with { LastVerifiedAt = TimeSpan.Zero }
        };
        Assert.Equal("Observation check overdue; continuity is unverified.",
            ProfileTimingPresentation.FromProfile(stale, TimeSpan.FromSeconds(20)).NextAction);
    }

    [Fact]
    public void Dashboard_reuses_row_objects_as_countdowns_change()
    {
        var viewModel = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        RecoverySnapshot initial = new RecoveryMachine(RecoveryPolicy.Default).Snapshot;
        HostedProfileStatus retry = Profile(initial with
        {
            State = RecoveryState.RetryWaiting,
            RetryDeadline = TimeSpan.FromSeconds(10)
        });
        viewModel.UpdateMonitoring(null, false, [retry], null, TimeSpan.FromSeconds(5));
        ApplicationStatusRow row = Assert.Single(viewModel.ApplicationRows);
        int changes = 0;
        row.PropertyChanged += (_, _) => changes++;

        viewModel.UpdateMonitoring(null, false, [retry], null, TimeSpan.FromSeconds(6));

        Assert.Same(row, Assert.Single(viewModel.ApplicationRows));
        Assert.Contains("4s", row.NextAction);
        Assert.True(changes > 0);
    }

    [Fact]
    public void Dashboard_filters_and_sorts_by_live_status_without_losing_profile_rows()
    {
        var viewModel = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        RecoverySnapshot initial = new RecoveryMachine(RecoveryPolicy.Default).Snapshot;
        HostedProfileStatus healthy = Profile(initial with
        {
            State = RecoveryState.Healthy,
            ReservedAutomaticAttempts = 1
        }) with { Name = "Zulu" };
        HostedProfileStatus locked = Profile(initial with
        {
            State = RecoveryState.AwaitingIntervention,
            LockedOut = true,
            ReservedAutomaticAttempts = 3
        }) with { Name = "Alpha" };
        HostedProfileStatus paused = Profile(initial with
        {
            State = RecoveryState.Healthy,
            Paused = true
        }) with { Name = "Mike" };
        viewModel.UpdateMonitoring(null, false, [healthy, locked, paused], null,
            TimeSpan.Zero);
        Assert.Equal(["Alpha", "Mike", "Zulu"],
            viewModel.ApplicationRows.Select(row => row.Name));

        viewModel.SelectedApplicationFilter = viewModel.ApplicationFilters.Single(option =>
            option.Category == ApplicationStatusCategory.Attention);
        ApplicationStatusRow lockedRow = Assert.Single(viewModel.ApplicationRows);
        Assert.Equal("Alpha", lockedRow.Name);
        Assert.Equal("1 shown of 3 configured", viewModel.ApplicationCountText);

        viewModel.SelectedApplicationFilter = viewModel.ApplicationFilters.Single(option =>
            option.Category == ApplicationStatusCategory.Recovering);
        Assert.Empty(viewModel.ApplicationRows);
        Assert.True(viewModel.HasNoVisibleApplications);

        viewModel.SelectedApplicationFilter = viewModel.ApplicationFilters[0];
        viewModel.SelectedApplicationSort = viewModel.ApplicationSorts.Single(option =>
            option.Mode == ApplicationSortMode.Attempts);
        Assert.Equal(["Alpha", "Zulu", "Mike"],
            viewModel.ApplicationRows.Select(row => row.Name));
        Assert.Same(lockedRow, viewModel.ApplicationRows[0]);
    }

    [Fact]
    public void Stop_and_pause_requires_a_verified_current_instance()
    {
        var viewModel = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        RecoverySnapshot snapshot = new RecoveryMachine(RecoveryPolicy.Default).Snapshot with
        {
            State = RecoveryState.Healthy,
            TargetIdentity = "verified-instance"
        };
        HostedProfileStatus profile = Profile(snapshot);
        viewModel.UpdateMonitoring(null, false, [profile], null, TimeSpan.Zero);
        Assert.True(Assert.Single(viewModel.ApplicationRows).CanStopAndPause);
        Assert.True(Assert.Single(viewModel.ApplicationRows).CanRestartNow);

        viewModel.UpdateMonitoring(null, false,
            [profile with { Recovery = snapshot with { DetectionUnavailable = true } }],
            null, TimeSpan.Zero);
        Assert.False(Assert.Single(viewModel.ApplicationRows).CanStopAndPause);
        Assert.False(Assert.Single(viewModel.ApplicationRows).CanRestartNow);

        viewModel.UpdateMonitoring(null, false,
            [profile with { Recovery = snapshot with
                { HoldReason = RecoveryHoldReason.InterruptedExplicitLaunch } }],
            null, TimeSpan.Zero);
        Assert.True(Assert.Single(viewModel.ApplicationRows).CanStopAndPause);
        Assert.False(Assert.Single(viewModel.ApplicationRows).CanRestartNow);
    }

    private static HostedProfileStatus Profile(RecoverySnapshot recovery) =>
        new(Guid.NewGuid(), "Disposable target", true, true, null, recovery, null,
            Policy: RecoveryPolicy.Default);
}
