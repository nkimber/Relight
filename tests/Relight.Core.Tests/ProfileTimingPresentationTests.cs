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

    private static HostedProfileStatus Profile(RecoverySnapshot recovery) =>
        new(Guid.NewGuid(), "Disposable target", true, true, null, recovery, null,
            Policy: RecoveryPolicy.Default);
}
