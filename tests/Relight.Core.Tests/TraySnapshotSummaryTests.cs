using Relight.Core;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class TraySnapshotSummaryTests
{
    [Fact]
    public void Icon_priority_tracks_attention_recovery_health_and_pause()
    {
        RecoverySnapshot baseSnapshot = new RecoveryMachine(RecoveryPolicy.Default).Snapshot;
        HostedProfileStatus healthy = Profile("Healthy", baseSnapshot with
        {
            State = RecoveryState.Healthy, Armed = true
        });
        HostedProfileStatus observing = Profile("Observing", baseSnapshot with
        {
            State = RecoveryState.Observing, Armed = true
        });
        HostedProfileStatus retrying = Profile("Retrying", baseSnapshot with
        {
            State = RecoveryState.RetryWaiting, Armed = true
        });
        HostedProfileStatus locked = Profile("Locked", baseSnapshot with
        {
            State = RecoveryState.AwaitingIntervention, LockedOut = true
        });
        HostedProfileStatus paused = Profile("Paused", baseSnapshot with
        {
            State = RecoveryState.Healthy, Paused = true
        });

        Assert.Equal(TrayIconState.Paused,
            TraySnapshotSummary.FromProfiles([], false, false).IconState);
        Assert.Equal(TrayIconState.Paused,
            TraySnapshotSummary.FromProfiles([paused], false, false).IconState);
        Assert.Equal(TrayIconState.Healthy,
            TraySnapshotSummary.FromProfiles([healthy], false, false).IconState);
        Assert.Equal(TrayIconState.Recovering,
            TraySnapshotSummary.FromProfiles([healthy, observing], false, false).IconState);
        Assert.Equal(TrayIconState.Recovering,
            TraySnapshotSummary.FromProfiles([retrying], false, false).IconState);
        TraySnapshotSummary attention = TraySnapshotSummary.FromProfiles(
            [healthy, observing, paused, locked], false, false);
        Assert.Equal(TrayIconState.Attention, attention.IconState);
        Assert.Equal(2, attention.Protected);
        Assert.Equal(1, attention.Observing);
        Assert.Equal(1, attention.Paused);
        Assert.Equal(1, attention.Alerts);
        Assert.Contains("2 protected", attention.Tooltip);
        Assert.Contains("1 observing", attention.Tooltip);
        Assert.True(attention.Tooltip.Length <= 63);
        Assert.Equal(TrayIconState.Attention,
            TraySnapshotSummary.FromProfiles([healthy], true, false).IconState);
    }

    [Fact]
    public void Tooltip_fits_notify_icon_even_at_supported_profile_limit()
    {
        RecoverySnapshot snapshot = new RecoveryMachine(RecoveryPolicy.Default).Snapshot with
        {
            State = RecoveryState.Observing
        };
        HostedProfileStatus[] profiles = Enumerable.Range(0, 500)
            .Select(index => Profile(index.ToString(), snapshot)).ToArray();
        TraySnapshotSummary summary = TraySnapshotSummary.FromProfiles(profiles, true, true);
        Assert.True(summary.Tooltip.Length <= 63);
        Assert.Contains("500 protected", summary.Tooltip);
        Assert.Contains("500 observing", summary.Tooltip);
    }

    private static HostedProfileStatus Profile(string name, RecoverySnapshot snapshot) =>
        new(Guid.NewGuid(), name, true, true, null, snapshot, null);
}
