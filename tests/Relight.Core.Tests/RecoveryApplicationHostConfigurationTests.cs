using Relight.Core;
using Relight.Engine;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class RecoveryApplicationHostConfigurationTests
{
    [Fact]
    public async Task First_run_creates_only_empty_configuration()
    {
        using var directory = new TestDirectory();
        await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(directory.Path, new FakeClock());
        Assert.NotNull(host.Configuration);
        Assert.Empty(host.GetProfiles());
        Assert.Null(host.ConfigurationProblem);
        Assert.True(File.Exists(Path.Combine(directory.Path, "configuration.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "State"), "*.json"));
    }

    [Fact]
    public async Task Existing_profile_without_state_never_gets_a_fresh_budget()
    {
        using var directory = new TestDirectory();
        Guid id = Guid.NewGuid();
        var configuration = new RelightConfiguration(
            [new(id, "Missing ledger", true,
                new(TargetKind.Executable, @"C:\Windows\System32\notepad.exe", []),
                RecoveryPolicy.Default)], GlobalConfiguration.Default);
        new ConfigurationStore(directory.Path).Initialize(configuration);

        var clock = new FakeClock();
        await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(directory.Path, clock);
        HostedProfileStatus status = Assert.Single(host.GetProfiles());
        Assert.True(status.Monitoring);
        Assert.False(status.AutomaticActionsAllowed);
        Assert.Contains("Recovery state is unavailable", status.Problem);
        Assert.Empty(host.Pulse());
        clock.Elapsed = TimeSpan.FromSeconds(30);
        await Assert.Single(host.Pulse()).Value;
        Assert.False(Assert.Single(host.GetProfiles()).AutomaticActionsAllowed);
        Assert.False(File.Exists(Path.Combine(directory.Path, "State", id.ToString("N") + ".json")));
    }

    [Fact]
    public async Task Damaged_current_configuration_keeps_last_good_visible_but_inert()
    {
        using var directory = new TestDirectory();
        Guid id = Guid.NewGuid();
        var configuration = new RelightConfiguration(
            [new(id, "Last good", true,
                new(TargetKind.Executable, @"C:\Windows\System32\notepad.exe", []),
                RecoveryPolicy.Default)], GlobalConfiguration.Default);
        var store = new ConfigurationStore(directory.Path);
        StoredConfiguration initial = store.Initialize(configuration);
        store.Save(initial, configuration);
        string path = Path.Combine(directory.Path, "configuration.json");
        File.WriteAllText(path, "{ damaged");

        await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(directory.Path, new FakeClock());
        Assert.True(host.Configuration?.FromLastGoodBackup);
        Assert.NotNull(host.ConfigurationProblem);
        HostedProfileStatus status = Assert.Single(host.GetProfiles());
        Assert.False(status.AutomaticActionsAllowed);
        Assert.Empty(host.Pulse());
        Assert.Equal("{ damaged", File.ReadAllText(path));
    }

    [Fact]
    public async Task Duplicate_is_disabled_with_new_identity_and_fresh_budget()
    {
        using var directory = new TestDirectory();
        Guid sourceId = Guid.NewGuid();
        Guid episodeId = Guid.NewGuid();
        var source = new ProfileConfiguration(sourceId, "Protected app", true,
            new(TargetKind.Executable, @"C:\Windows\System32\notepad.exe", []),
            RecoveryPolicy.Default);
        new ConfigurationStore(directory.Path).Initialize(
            new([source], GlobalConfiguration.Default));
        var state = new RecoveryStateStore(directory.Path);
        RecoveryCheckpoint sourceCheckpoint = new(true, false, true, true, 2,
            episodeId, RecoveryState.AwaitingIntervention, null);
        state.Create(sourceId, sourceCheckpoint);

        Guid duplicateId;
        await using (var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(directory.Path,
                         new FakeClock()))
        {
            duplicateId = await host.DuplicateProfileAsync(sourceId);
            Assert.NotEqual(sourceId, duplicateId);
            HostedProfileStatus copy = Assert.Single(host.GetProfiles(),
                item => item.Id == duplicateId);
            Assert.False(copy.ConfiguredEnabled);
            Assert.False(copy.Monitoring);
            Assert.False(copy.AutomaticActionsAllowed);
            Assert.Equal(RecoveryState.Disabled, copy.Recovery?.State);
            Assert.DoesNotContain(duplicateId, host.Pulse().Keys);
        }

        StoredConfiguration saved = new ConfigurationStore(directory.Path).Load();
        ProfileConfiguration duplicate = Assert.Single(saved.Configuration.Profiles,
            item => item.Id == duplicateId);
        Assert.Equal("Copy of Protected app", duplicate.Name);
        Assert.False(duplicate.Enabled);
        Assert.Equal(source.Target.Kind, duplicate.Target.Kind);
        Assert.Equal(source.Target.Identity, duplicate.Target.Identity);
        Assert.Equal(source.Target.Arguments, duplicate.Target.Arguments);
        Assert.Equal(source.Policy, duplicate.Policy);
        Assert.Equal(sourceCheckpoint, state.Load(sourceId).Checkpoint);
        RecoveryCheckpoint fresh = state.Load(duplicateId).Checkpoint;
        Assert.False(fresh.Enabled);
        Assert.Equal(0, fresh.ReservedAutomaticAttempts);
        Assert.False(fresh.LockedOut);
        Assert.Null(fresh.EpisodeId);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"relight-host-config-{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
