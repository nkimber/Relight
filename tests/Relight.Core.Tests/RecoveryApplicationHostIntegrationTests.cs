using System.Diagnostics;
using Relight.Core;
using Relight.Engine;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

[Collection("Windows desktop process tests")]
public sealed class RecoveryApplicationHostIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Sign_in_preference_saves_with_registry_and_rolls_back_on_stale_configuration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new FakeStartupRegistry();
            var startup = new CurrentUserStartupRegistration(@"C:\Relight\Relight.exe", registry);
            await using (var host = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock()))
            {
                await host.SetStartAtSignInAsync(true, startup);
                Assert.True(host.Configuration?.Configuration.Settings.StartAtSignIn);
                Assert.True(startup.Inspect().EnabledForThisExecutable);

                var store = new ConfigurationStore(root);
                StoredConfiguration current = store.Load();
                store.Save(current, current.Configuration);
                await Assert.ThrowsAsync<StaleConfigurationException>(() =>
                    host.SetStartAtSignInAsync(true, startup));
                Assert.True(startup.Inspect().EnabledForThisExecutable);
                await Assert.ThrowsAsync<StaleConfigurationException>(() =>
                    host.SetStartAtSignInAsync(false, startup));
                Assert.True(startup.Inspect().EnabledForThisExecutable);
                Assert.True(host.Configuration?.Configuration.Settings.StartAtSignIn);
            }
            await using var reopened = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            Assert.True(reopened.Configuration?.Configuration.Settings.StartAtSignIn);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Pause_all_keeps_other_profiles_independent_when_one_ledger_fails()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-pause-all-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string secondExecutable = Path.Combine(root, "SecondTarget.exe");
            File.Copy(TestExecutable(), secondExecutable);
            await using var host = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            Guid first = await host.RegisterExecutableAsync("First target", TestExecutable());
            Guid second = await host.RegisterExecutableAsync("Second target", secondExecutable);
            var stateStore = new RecoveryStateStore(root);
            StoredRecoveryState stale = stateStore.Load(first);
            stateStore.Save(first, stale.Revision, stale.Checkpoint);

            ProfileBatchResult paused = await host.SetAllPausedAsync(true);

            Assert.Equal(2, paused.Requested);
            Assert.Equal(1, paused.Completed);
            Assert.Single(paused.Errors);
            Assert.Contains("First target", paused.Errors[0]);
            Assert.False(host.GetProfiles().Single(profile => profile.Id == first)
                .AutomaticActionsAllowed);
            Assert.False(stateStore.Load(first).Checkpoint.Paused);
            Assert.True(host.GetProfiles().Single(profile => profile.Id == second).Recovery?.Paused);
            Assert.True(stateStore.Load(second).Checkpoint.Paused);

            ProfileBatchResult resumed = await host.SetAllPausedAsync(false);
            Assert.Equal(1, resumed.Requested);
            Assert.Equal(1, resumed.Completed);
            Assert.Empty(resumed.Errors);
            Assert.False(stateStore.Load(second).Checkpoint.Paused);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Explicit_repair_reopens_last_good_profile_with_its_existing_state()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var configured = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock()))
                id = await configured.RegisterExecutableAsync("Disposable target", TestExecutable());
            var stateStore = new RecoveryStateStore(root);
            StoredRecoveryState initialState = stateStore.Load(id);
            StoredRecoveryState existingState = stateStore.Save(id, initialState.Revision,
                initialState.Checkpoint);
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            store.Save(current, current.Configuration);
            string file = Path.Combine(root, "configuration.json");
            File.WriteAllText(file, "{ invalid external edit");
            string? archived;
            await using (var degraded = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock()))
            {
                Assert.True(degraded.Configuration?.FromLastGoodBackup);
                Assert.NotNull(degraded.ConfigurationProblem);
                archived = await degraded.RepairConfigurationAsync();
            }
            Assert.NotNull(archived);
            Assert.Equal("{ invalid external edit", File.ReadAllText(archived));
            await using var reopened = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            Assert.False(reopened.Configuration?.FromLastGoodBackup);
            Assert.Null(reopened.ConfigurationProblem);
            Assert.Equal(id, Assert.Single(reopened.GetProfiles()).Id);
            Assert.NotNull(Assert.Single(reopened.GetProfiles()).Recovery);
            Assert.Equal(existingState, stateStore.Load(id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Registration_creates_ledger_before_enabling_and_rejects_overlap()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-register-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using (var host = await RecoveryApplicationHost.OpenAsync(root, new FakeClock()))
            {
                string executable = TestExecutable();
                Detection detection = await RecoveryApplicationHost.InspectExecutableAsync(executable);
                Assert.NotEqual(DetectionKind.Unavailable, detection.Kind);
                Guid id = await host.RegisterExecutableAsync("Disposable target", executable);

                ProfileConfiguration saved = Assert.Single(
                    new ConfigurationStore(root).Load().Configuration.Profiles);
                Assert.Equal(id, saved.Id);
                Assert.True(saved.Enabled);
                Assert.False(saved.Policy.StartAutomaticallyWhenInitiallyAbsent);
                Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                    .Checkpoint.ReservedAutomaticAttempts);
                Assert.True(Assert.Single(host.GetProfiles()).AutomaticActionsAllowed);
                await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(RecoveryState.WaitingForFirstStart,
                    Assert.Single(host.GetProfiles()).Recovery?.State);
                await host.SetProfilePausedAsync(id, true);
                Assert.True(Assert.Single(host.GetProfiles()).Recovery?.Paused);
                Assert.True(new RecoveryStateStore(root).Load(id).Checkpoint.Paused);
                await host.SetProfilePausedAsync(id, false);
                Assert.False(Assert.Single(host.GetProfiles()).Recovery?.Paused);
                await host.ResetProfileRecoveryAsync(id);
                Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                    .Checkpoint.ReservedAutomaticAttempts);
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    host.RegisterExecutableAsync("Duplicate", executable));
            }

            await using var reopened = await RecoveryApplicationHost.OpenAsync(root, new FakeClock());
            Assert.True(Assert.Single(reopened.GetProfiles()).AutomaticActionsAllowed);
            Assert.Single(new ConfigurationStore(root).Load().Configuration.Profiles);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Stale_configuration_does_not_publish_new_profile_or_refund_its_ledger()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-register-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenAsync(root, new FakeClock());
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            store.Save(current, current.Configuration);

            await Assert.ThrowsAsync<StaleConfigurationException>(() =>
                host.RegisterExecutableAsync("Disposable target", TestExecutable()));
            Assert.Empty(host.GetProfiles());
            Assert.Empty(store.Load().Configuration.Profiles);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "State"), "*.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Stale_disable_restores_enabled_ledger_and_keeps_profile_scheduled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-disable-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenAsync(root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
            var configuration = new ConfigurationStore(root);
            StoredConfiguration current = configuration.Load();
            configuration.Save(current, current.Configuration);

            await Assert.ThrowsAsync<StaleConfigurationException>(() =>
                host.SetProfileEnabledAsync(id, false));
            Assert.True(new RecoveryStateStore(root).Load(id).Checkpoint.Enabled);
            Assert.True(Assert.Single(host.GetProfiles()).ConfiguredEnabled);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Stale_policy_edit_keeps_old_live_policy_and_profile_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-edit-stale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenAsync(root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
            RecoveryPolicy original = host.GetProfileForEdit(id).Policy;
            var configuration = new ConfigurationStore(root);
            StoredConfiguration current = configuration.Load();
            configuration.Save(current, current.Configuration);

            await Assert.ThrowsAsync<StaleConfigurationException>(() =>
                host.UpdateProfileBasicsAsync(id, "Renamed target", original with
                {
                    RetryDelay = TimeSpan.FromSeconds(5)
                }));
            Assert.Equal(original, host.GetProfileForEdit(id).Policy);
            Assert.Equal("Disposable target", Assert.Single(host.GetProfiles()).Name);
            Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                .Checkpoint.ReservedAutomaticAttempts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Disabled_profile_reopens_with_its_existing_ledger()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var host = await RecoveryApplicationHost.OpenAsync(root, new FakeClock()))
            {
                id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
                await host.SetProfileEnabledAsync(id, false);
                Assert.False(Assert.Single(host.GetProfiles()).ConfiguredEnabled);
            }

            await using (var reopened = await RecoveryApplicationHost.OpenAsync(root,
                             new FakeClock()))
            {
                HostedProfileStatus disabled = Assert.Single(reopened.GetProfiles());
                Assert.False(disabled.ConfiguredEnabled);
                Assert.Equal(RecoveryState.Disabled, disabled.Recovery?.State);
                Assert.Empty(reopened.Pulse());
                RecoveryPolicy editedPolicy = reopened.GetProfileForEdit(id).Policy with
                {
                    RetryDelay = TimeSpan.FromSeconds(5)
                };
                await reopened.UpdateProfileBasicsAsync(id, "Renamed while disabled",
                    editedPolicy);
                await reopened.SetProfileEnabledAsync(id, true);
                Assert.True(Assert.Single(reopened.GetProfiles()).ConfiguredEnabled);
                Assert.Equal("Renamed while disabled", Assert.Single(reopened.GetProfiles()).Name);
                Assert.Equal(editedPolicy, reopened.GetProfileForEdit(id).Policy);
                Assert.True(new RecoveryStateStore(root).Load(id).Checkpoint.Enabled);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Host_recovers_disposable_target_and_leaves_it_running_on_exit()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-{Guid.NewGuid():N}");
        string ready = Path.Combine(root, "target.ready");
        Directory.CreateDirectory(root);
        int? pid = null;
        DateTime? startedUtc = null;
        try
        {
            Guid id = Guid.NewGuid();
            string label = $"host-{Guid.NewGuid():N}";
            var policy = RecoveryPolicy.Default with
            {
                StartAutomaticallyWhenInitiallyAbsent = true,
                RetryDelay = TimeSpan.FromSeconds(5)
            };
            var config = new RelightConfiguration(
                [new(id, "Disposable target", true,
                    new(TargetKind.Executable, TestExecutable(),
                        ["--label", label, "--ready-file", ready,
                         "--exit-after-ms", "30000"], RequiredArgument: label),
                    policy)], GlobalConfiguration.Default);
            new ConfigurationStore(root).Initialize(config);
            new RecoveryStateStore(root).Create(id, new RecoveryMachine(policy).ExportCheckpoint());
            var clock = new FakeClock();

            await using (var host = await RecoveryApplicationHost.OpenAsync(root, clock))
            {
                HostedProfileStatus status = Assert.Single(host.GetProfiles());
                Assert.True(status.Monitoring);
                foreach (int second in new[] { 0, 2, 7 })
                {
                    clock.Elapsed = TimeSpan.FromSeconds(second);
                    await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
                }
                string[] identity = (await WaitForFile(ready)).Split('|');
                pid = int.Parse(identity[0]);
                startedUtc = DateTime.Parse(identity[1]).ToUniversalTime();
                clock.Elapsed = TimeSpan.FromSeconds(8);
                host.RequestImmediate(id);
                await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(RecoveryState.Observing,
                    Assert.Single(host.GetProfiles()).Recovery?.State);
                Assert.Equal(1, new RecoveryStateStore(root).Load(id)
                    .Checkpoint.ReservedAutomaticAttempts);
                await host.SetProfilePausedAsync(id, true);
                Assert.True(new RecoveryStateStore(root).Load(id).Checkpoint.Paused);
                using Process stillRunning = Process.GetProcessById(pid.Value);
                Assert.False(stillRunning.HasExited);

                await host.SetProfileEnabledAsync(id, false);
                Assert.False(Assert.Single(host.GetProfiles()).ConfiguredEnabled);
                RecoveryCheckpoint disabled = new RecoveryStateStore(root).Load(id).Checkpoint;
                Assert.False(disabled.Enabled);
                Assert.Equal(1, disabled.ReservedAutomaticAttempts);
                Assert.False(stillRunning.HasExited);

                await host.SetProfileEnabledAsync(id, true);
                Assert.True(Assert.Single(host.GetProfiles()).ConfiguredEnabled);
                Assert.Equal(1, new RecoveryStateStore(root).Load(id)
                    .Checkpoint.ReservedAutomaticAttempts);
                RecoveryPolicy editedPolicy = policy with
                {
                    ObservationPeriod = TimeSpan.FromMinutes(1),
                    RetryDelay = TimeSpan.FromSeconds(7)
                };
                await host.UpdateProfileBasicsAsync(id, "Renamed disposable target",
                    editedPolicy);
                Assert.Equal(id, host.GetProfileForEdit(id).Id);
                Assert.Equal(editedPolicy, host.GetProfileForEdit(id).Policy);
                Assert.Equal("Renamed disposable target", Assert.Single(host.GetProfiles()).Name);
                Assert.Equal(1, new RecoveryStateStore(root).Load(id)
                    .Checkpoint.ReservedAutomaticAttempts);
                Assert.False(stillRunning.HasExited);

                await host.RemoveProfileAsync(id);
                Assert.Empty(host.GetProfiles());
                Assert.Empty(new ConfigurationStore(root).Load().Configuration.Profiles);
                Assert.False(new RecoveryStateStore(root).Load(id).Checkpoint.Enabled);
                Assert.False(stillRunning.HasExited);
            }

            using Process target = Process.GetProcessById(pid.Value);
            Assert.False(target.HasExited);
            Assert.Equal(startedUtc.Value.Ticks, target.StartTime.ToUniversalTime().Ticks);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "Logs"), "*.jsonl"));
        }
        finally
        {
            if (pid is null && File.Exists(ready))
            {
                string[] identity = File.ReadAllText(ready).Split('|');
                if (identity.Length == 2 && int.TryParse(identity[0], out int readyPid) &&
                    DateTime.TryParse(identity[1], out DateTime readyStarted))
                {
                    pid = readyPid;
                    startedUtc = readyStarted.ToUniversalTime();
                }
            }
            if (pid is not null && startedUtc is not null)
            {
                try
                {
                    using Process target = Process.GetProcessById(pid.Value);
                    if (!target.HasExited &&
                        target.StartTime.ToUniversalTime().Ticks == startedUtc.Value.Ticks)
                    {
                        target.Kill();
                        await target.WaitForExitAsync();
                    }
                }
                catch (ArgumentException) { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Host_stop_and_pause_requires_force_choice_for_blocked_test_target()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "target.ready");
        string label = $"host-stop-{Guid.NewGuid():N}";
        int? pid = null;
        try
        {
            Guid id = Guid.NewGuid();
            var policy = RecoveryPolicy.Default;
            string[] arguments = ["--label", label, "--ready-file", ready,
                "--exit-after-ms", "30000", "--block-close"];
            var target = new ExecutableTarget(TestExecutable(), arguments,
                RequiredArgument: label);
            new ConfigurationStore(root).Initialize(new(
                [new(id, "Blocked disposable target", true,
                    new(TargetKind.Executable, target.CanonicalPath, [.. arguments],
                        RequiredArgument: label), policy)], GlobalConfiguration.Default));
            new RecoveryStateStore(root).Create(id,
                new RecoveryMachine(policy).ExportCheckpoint());
            await new ExecutableLauncher(target).LaunchAsync(Guid.NewGuid(),
                CancellationToken.None);
            pid = int.Parse((await WaitForFile(ready)).Split('|')[0]);

            await using var host = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(Assert.Single(host.GetProfiles()).Recovery?.TargetIdentity);

            StopCommandResult result = await host.StopProfileAndPauseAsync(id,
                TimeSpan.FromMilliseconds(300));
            Assert.Equal(TargetStopOutcome.NeedsForceChoice, result.Stop.Outcome);
            Assert.True(new RecoveryStateStore(root).Load(id).Checkpoint.Paused);
            using (Process running = Process.GetProcessById(pid.Value))
                Assert.False(running.HasExited);

            TargetStopResult forced = await host.ForceClosePausedProfileAsync(id,
                result.OperationId, result.SelectedIdentity!);
            Assert.Equal(TargetStopOutcome.Stopped, forced.Outcome);
            Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                .Checkpoint.ReservedAutomaticAttempts);
            Assert.Equal(DetectionKind.Absent, (await new ExecutableDiscovery(target)
                .DetectAsync(CancellationToken.None)).Kind);
        }
        finally
        {
            if (pid is not null)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid.Value);
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
                catch (ArgumentException) { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string TestExecutable()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Relight.slnx")))
            root = Directory.GetParent(root)?.FullName;
        if (root is null) throw new DirectoryNotFoundException("Solution root not found.");
        string configuration = AppContext.BaseDirectory.Contains("\\Debug\\", StringComparison.OrdinalIgnoreCase)
            ? "Debug" : "Release";
        return Path.Combine(root, "tests", "Relight.TestTarget", "bin", configuration,
            "net10.0-windows", "Relight.TestTarget.exe");
    }

    private static async Task<string> WaitForFile(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.Token.IsCancellationRequested)
        {
            if (File.Exists(path)) return await File.ReadAllTextAsync(path, timeout.Token);
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("Disposable target did not report readiness.");
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class FakeStartupRegistry : IStartupRegistryValue
    {
        private string? _command;
        public string? Read() => _command;
        public void Write(string? command) => _command = command;
    }
}
