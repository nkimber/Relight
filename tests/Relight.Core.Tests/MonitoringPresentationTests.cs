using Relight.Core;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class MonitoringPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 14, 0, 0, TimeSpan.Zero);

    private static HostedProfileStatus Profile()
    {
        var recovery = new RecoveryMachine(RecoveryPolicy.Default).Snapshot with
        {
            State = RecoveryState.Observing,
            TargetIdentity = $"1|{ChatGptPackagedDiscovery.PackageFamilyName}|123|{Now.AddHours(-26).Ticks}",
            LastVerifiedAt = TimeSpan.FromSeconds(30),
            ObservationStartedAt = TimeSpan.FromSeconds(30)
        };
        return new(Guid.NewGuid(), "ChatGPT", true, true, null, recovery, null,
            Policy: RecoveryPolicy.Default, TargetKind: TargetKind.PackagedApplication);
    }

    [Fact]
    public void Process_age_is_frozen_at_last_sighting_and_never_claims_unverified_uptime()
    {
        var model = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        HostedProfileStatus profile = Profile();
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.FromSeconds(90), nowUtc: Now);
        MonitoringRow row = Assert.Single(model.MonitoringRows);
        Assert.Equal("123", row.ProcessId);
        Assert.Equal("1d 1h 59m", row.Uptime);
        Assert.Equal("1m 0s ago", row.LastVerified);
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.FromSeconds(150), nowUtc: Now.AddMinutes(1));
        Assert.Same(row, Assert.Single(model.MonitoringRows));
        Assert.Equal("1d 1h 59m", row.Uptime);
        foreach (HostedProfileStatus unverified in new[]
        {
            profile with { Recovery = profile.Recovery! with { DetectionUnavailable = true } },
            profile with { Recovery = profile.Recovery! with { Paused = true } },
            profile with { ConfiguredEnabled = false },
            profile with { Recovery = profile.Recovery! with { TargetIdentity = null } },
            profile with { Recovery = profile.Recovery! with { LastVerifiedAt = null } },
            profile with { Problem = "Storage unavailable" }
        })
        {
            model.UpdateMonitoring(null, false, [unverified], null, TimeSpan.FromSeconds(90), nowUtc: Now);
            Assert.Equal("—", Assert.Single(model.MonitoringRows).Uptime);
            Assert.Equal("—", Assert.Single(model.MonitoringRows).ProcessId);
        }
    }

    [Fact]
    public void Main_page_includes_all_profiles_independently_of_application_filters_and_removes_old_rows()
    {
        var model = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        Assert.True(model.IsMonitoring);
        HostedProfileStatus running = Profile();
        HostedProfileStatus disabled = Profile() with { Name = "Disabled", ConfiguredEnabled = false, Monitoring = false };
        model.UpdateMonitoring(null, false, [running, disabled], null, TimeSpan.Zero, nowUtc: Now);
        model.SelectedApplicationFilter = model.ApplicationFilters.Single(option =>
            option.Category == ApplicationStatusCategory.Attention);
        Assert.Empty(model.ApplicationRows);
        Assert.Equal(2, model.MonitoringRows.Count);
        model.Navigate(ShellPage.Applications);
        Assert.False(model.IsMonitoring);
        model.Navigate(ShellPage.Monitoring);
        Assert.Equal("Monitoring", model.Heading);
        model.UpdateMonitoring(null, false, [running], null, TimeSpan.Zero, nowUtc: Now);
        Assert.Equal(running.Id, Assert.Single(model.MonitoringRows).Id);
    }

    [Fact]
    public void History_failure_and_degradation_do_not_masquerade_as_zero_relaunches()
    {
        var model = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        HostedProfileStatus profile = Profile();
        var history = new DashboardEventHistory(new Dictionary<Guid, DashboardEventMilestones>
        {
            [profile.Id] = new(null, null, 4)
        }, 0, Now);
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.Zero, history, nowUtc: Now);
        Assert.Equal("4", Assert.Single(model.MonitoringRows).AutomaticRelaunches);
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.Zero,
            history with { SkippedMalformedLines = 1 }, nowUtc: Now);
        Assert.Equal("4 (partial)", Assert.Single(model.MonitoringRows).AutomaticRelaunches);
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.Zero, history, "Read failed", nowUtc: Now);
        Assert.Equal("Unavailable", Assert.Single(model.MonitoringRows).AutomaticRelaunches);
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.Zero, nowUtc: Now);
        Assert.Equal("Loading…", Assert.Single(model.MonitoringRows).AutomaticRelaunches);
    }

    [Theory]
    [InlineData("1|target|12|9999999999999999999")]
    [InlineData("1|target|12|-1")]
    [InlineData("opaque-instance")]
    [InlineData("1|OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0|12|639267120000000000")]
    public void Unknown_or_malformed_adapter_identity_has_no_process_metadata(string identity)
        => Assert.Null(WindowsProcessInstance.FromIdentity(identity, TargetKind.PackagedApplication));

    [Fact]
    public void Replaced_executable_instance_uses_its_new_start_time_even_if_PID_is_reused()
    {
        var model = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
        HostedProfileStatus profile = Profile() with { TargetKind = TargetKind.Executable };
        profile = profile with { Recovery = profile.Recovery! with
        {
            TargetIdentity = $"1|C:\\apps\\example.exe|123|{Now.AddHours(-2).Ticks}",
            LastVerifiedAt = TimeSpan.FromSeconds(30)
        } };
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.FromSeconds(30), nowUtc: Now);
        MonitoringRow row = Assert.Single(model.MonitoringRows);
        Assert.Equal("2h 0m", row.Uptime);
        profile = profile with { Recovery = profile.Recovery! with
        {
            TargetIdentity = $"1|C:\\apps\\example.exe|123|{Now.AddMinutes(1).Ticks}",
            LastVerifiedAt = TimeSpan.FromMinutes(2)
        } };
        model.UpdateMonitoring(null, false, [profile], null, TimeSpan.FromMinutes(2), nowUtc: Now.AddMinutes(2));
        Assert.Same(row, Assert.Single(model.MonitoringRows));
        Assert.Equal("123", row.ProcessId);
        Assert.Equal("1m 0s", row.Uptime);
    }
}
