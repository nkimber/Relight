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
    public async Task Editing_executable_identity_preserves_budget_and_rebinds_profile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        int? oldPid = null;
        try
        {
            string original = TestExecutable();
            string replacement = Path.Combine(root, "Replacement.exe");
            string ready = Path.Combine(root, "original.ready");
            File.Copy(TestExecutable(), replacement);
            await using var host = await RecoveryApplicationHost.OpenAsync(root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Original", original);
            await new ExecutableLauncher(new ExecutableTarget(original,
                ["--ready-file", ready, "--exit-after-ms", "30000"]))
                .LaunchAsync(Guid.NewGuid(), CancellationToken.None);
            oldPid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            var stateStore = new RecoveryStateStore(root);
            StoredRecoveryState before = stateStore.Load(id);
            var target = new TargetConfiguration(TargetKind.Executable, replacement, []);

            await host.UpdateProfileDefinitionAsync(id, "Replacement", target,
                host.GetProfileForEdit(id).Policy, true, true);

            ProfileConfiguration edited = host.GetProfileForEdit(id);
            Assert.Equal(id, edited.Id);
            Assert.Equal("Replacement", edited.Name);
            Assert.Equal(replacement, edited.Target.Identity);
            Assert.Equal(before, stateStore.Load(id));
            Assert.True(host.GetProfiles().Single(item => item.Id == id)
                .AutomaticActionsAllowed);
            using (Process old = Process.GetProcessById(oldPid.Value))
                Assert.False(old.HasExited);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DetectionKind.Absent,
                (await new ExecutableDiscovery(new ExecutableTarget(replacement, []))
                    .DetectAsync(CancellationToken.None)).Kind);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.UpdateProfileDefinitionAsync(id, "Invalid",
                    new(TargetKind.PackagedApplication, "other", []),
                    edited.Policy, true, true));
            Assert.Equal(replacement, host.GetProfileForEdit(id).Target.Identity);
        }
        finally
        {
            if (oldPid is not null)
            {
                try
                {
                    using Process old = Process.GetProcessById(oldPid.Value);
                    if (!old.HasExited)
                    {
                        old.Kill();
                        await old.WaitForExitAsync();
                    }
                }
                catch (ArgumentException) { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
                Guid id = await host.RegisterExecutableAsync("Disposable target", executable,
                    ["--label", "hello world"], root);

                ProfileConfiguration saved = Assert.Single(
                    new ConfigurationStore(root).Load().Configuration.Profiles);
                Assert.Equal(id, saved.Id);
                Assert.Equal(new[] { "--label", "hello world" }, saved.Target.Arguments);
                Assert.Equal(root, saved.Target.WorkingDirectory);
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
    public async Task Shared_host_registers_and_reopens_fresh_profile_without_legacy_state()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-shared-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
            {
                id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
                Assert.True(Assert.Single(host.GetProfiles()).AutomaticActionsAllowed);
                await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
                await host.SetProfilePausedAsync(id, true);
            }

            SharedRecoveryBudget budget = new SharedRecoveryBudgetStore(root).Load(id);
            Assert.Equal(0, budget.ReservedAutomaticAttempts);
            Assert.False(File.Exists(Path.Combine(root, "State", $"{id:N}.json")));
            var session = new RecoverySessionStateStore(root,
                WindowsLogonSessionIdentity.Current().StorageKey,
                new SharedRecoveryBudgetStore(root));
            Assert.True(session.Load(id).Checkpoint.Paused);

            await using var reopened = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            HostedProfileStatus status = Assert.Single(reopened.GetProfiles());
            Assert.True(status.AutomaticActionsAllowed, status.Problem);
            Assert.True(status.Recovery?.Paused);
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_suspends_discovery_after_external_configuration_change()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-config-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, clock);
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Assert.Single(host.GetProfiles()).Recovery?.DetectionUnavailable);

            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            store.Save(current, current.Configuration with { Profiles = [] });
            clock.Elapsed = TimeSpan.FromMinutes(5);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            HostedProfileStatus status = Assert.Single(host.GetProfiles());
            Assert.Equal(id, status.Id);
            Assert.True(status.Recovery?.DetectionUnavailable);
            await Assert.ThrowsAsync<ConfigurationUnavailableException>(() =>
                host.StartProfileNowAsync(id));
            await Assert.ThrowsAsync<ConfigurationUnavailableException>(() =>
                host.StopProfileAndPauseAsync(id, TimeSpan.FromSeconds(1)));
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_reconciles_external_edit_and_removal_without_resetting_budget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-config-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Original", TestExecutable());
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            ProfileConfiguration original = Assert.Single(current.Configuration.Profiles);
            RecoveryPolicy revisedPolicy = original.Policy with
            {
                MaximumAutomaticAttempts = 1
            };
            store.Save(current, current.Configuration with
            {
                Profiles = [original with { Name = "Revised", Policy = revisedPolicy }]
            });

            Assert.True(await host.ReconcileSharedConfigurationAsync());
            HostedProfileStatus revised = Assert.Single(host.GetProfiles());
            Assert.Equal("Revised", revised.Name);
            Assert.Equal(revisedPolicy, revised.Policy);
            Assert.True(revised.AutomaticActionsAllowed, revised.Problem);
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);

            StoredConfiguration updated = store.Load();
            store.Save(updated, updated.Configuration with { Profiles = [] });
            Assert.True(await host.ReconcileSharedConfigurationAsync());
            Assert.Empty(host.GetProfiles());
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);
            Assert.False(await host.ReconcileSharedConfigurationAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_keeps_externally_added_profile_passive_without_budget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-config-new-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            Guid id = Guid.NewGuid();
            var profile = new ProfileConfiguration(id, "External", true,
                new TargetConfiguration(TargetKind.Executable, TestExecutable(), []),
                RecoveryPolicy.Default);
            store.Save(current, current.Configuration with { Profiles = [profile] });

            Assert.True(await host.ReconcileSharedConfigurationAsync());
            HostedProfileStatus added = Assert.Single(host.GetProfiles());
            Assert.Equal(id, added.Id);
            Assert.False(added.AutomaticActionsAllowed);
            Assert.Contains("budget", added.Problem, StringComparison.OrdinalIgnoreCase);
            Assert.False(new SharedRecoveryBudgetStore(root).HasBudgetEvidence(id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_adopts_externally_added_profile_with_trusted_budget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-config-adopt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            Guid id = Guid.NewGuid();
            var budgets = new SharedRecoveryBudgetStore(root);
            budgets.Create(id);
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            var profile = new ProfileConfiguration(id, "External", true,
                new TargetConfiguration(TargetKind.Executable, TestExecutable(), []),
                RecoveryPolicy.Default);
            store.Save(current, current.Configuration with { Profiles = [profile] });

            Assert.True(await host.ReconcileSharedConfigurationAsync());
            HostedProfileStatus added = Assert.Single(host.GetProfiles());
            Assert.True(added.AutomaticActionsAllowed, added.Problem);
            Assert.Equal(id, added.Id);
            var session = new RecoverySessionStateStore(root,
                WindowsLogonSessionIdentity.Current().StorageKey, budgets);
            Assert.True(session.Load(id).Checkpoint.Enabled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_run_loop_reloads_external_configuration_without_manual_call()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-config-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Original", TestExecutable());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task running = host.RunAsync(stop.Token);
            try
            {
                var store = new ConfigurationStore(root);
                StoredConfiguration current = store.Load();
                ProfileConfiguration original = Assert.Single(current.Configuration.Profiles);
                store.Save(current, current.Configuration with
                {
                    Profiles = [original with { Name = "From another session" }]
                });

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                while (host.GetProfiles().SingleOrDefault()?.Name != "From another session")
                    await Task.Delay(50, timeout.Token);

                HostedProfileStatus updated = Assert.Single(host.GetProfiles());
                Assert.Equal(id, updated.Id);
                Assert.True(updated.AutomaticActionsAllowed, updated.Problem);
                Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                    .ReservedAutomaticAttempts);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_watcher_suspends_on_invalid_configuration_then_recovers_after_repair()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-config-watch-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            store.Save(current, current.Configuration);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task running = host.RunAsync(stop.Token);
            try
            {
                File.WriteAllText(Path.Combine(root, "configuration.json"), "invalid configuration");
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
                    while (host.ConfigurationProblem is null)
                        await Task.Delay(50, timeout.Token);
                await Assert.ThrowsAsync<ConfigurationUnavailableException>(() =>
                    host.StartProfileNowAsync(id));

                StoredConfiguration backup = store.Load();
                Assert.True(backup.FromLastGoodBackup);
                store.RepairFromLastGood(backup);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
                    while (host.ConfigurationProblem is not null)
                        await Task.Delay(50, timeout.Token);
                Assert.True(Assert.Single(host.GetProfiles()).AutomaticActionsAllowed);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_duplicate_and_enable_keep_separate_budgets()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-shared-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            Guid source = await host.RegisterExecutableAsync("Source", TestExecutable());
            Guid copy = await host.DuplicateProfileAsync(source);
            var budgets = new SharedRecoveryBudgetStore(root);
            Assert.Equal(0, budgets.Load(source).ReservedAutomaticAttempts);
            Assert.Equal(0, budgets.Load(copy).ReservedAutomaticAttempts);
            var session = new RecoverySessionStateStore(root,
                WindowsLogonSessionIdentity.Current().StorageKey, budgets);
            Assert.False(session.Load(copy).Checkpoint.Enabled);
            Assert.False(File.Exists(Path.Combine(root, "State", $"{copy:N}.json")));

            await host.SetProfileEnabledAsync(source, false);
            await host.SetProfileEnabledAsync(copy, true);
            HostedProfileStatus enabledCopy = host.GetProfiles().Single(profile =>
                profile.Id == copy);
            Assert.True(enabledCopy.AutomaticActionsAllowed, enabledCopy.Problem);
            Assert.True(session.Load(copy).Checkpoint.Enabled);
            Assert.Equal(0, budgets.Load(copy).ReservedAutomaticAttempts);
            Assert.Equal(0, budgets.Load(source).ReservedAutomaticAttempts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_requires_guarded_legacy_migration_and_preserves_original()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var legacy = await RecoveryApplicationHost.OpenAsync(root,
                             new FakeClock()))
            {
                id = await legacy.RegisterExecutableAsync("Disposable target", TestExecutable());
                await legacy.SetProfilePausedAsync(id, true);
            }
            string oldPath = Path.Combine(root, "State", $"{id:N}.json");
            byte[] original = File.ReadAllBytes(oldPath);
            await using (var guarded = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
            {
                HostedProfileStatus blocked = Assert.Single(guarded.GetProfiles());
                Assert.False(blocked.AutomaticActionsAllowed);
                Assert.Contains("guarded migration", blocked.Problem);
                Assert.False(new SharedRecoveryBudgetStore(root).HasBudgetEvidence(id));
            }

            await using (var migrated = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock(), allowLegacyMigration: true))
            {
                HostedProfileStatus ready = Assert.Single(migrated.GetProfiles());
                Assert.True(ready.AutomaticActionsAllowed, ready.Problem);
                Assert.True(ready.Recovery?.Paused);
                Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                    .ReservedAutomaticAttempts);
            }
            Assert.Contains("legacy access disabled", File.ReadAllText(oldPath));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(root,
                "State", $"{id:N}.legacy.json")));
            Assert.Equal(LegacyStateOwnership.SessionOwner,
                new RecoveryStateStore(root).GetOwnership(id));

            await using var reopened = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            Assert.True(Assert.Single(reopened.GetProfiles()).AutomaticActionsAllowed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_does_not_recreate_missing_budget_or_finish_partial_migration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-state-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var shared = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
                id = await shared.RegisterExecutableAsync("Disposable target", TestExecutable());
            string sessionDirectory = Path.Combine(root, "Sessions",
                WindowsLogonSessionIdentity.Current().StorageKey, "State");
            string sessionPath = Path.Combine(sessionDirectory, $"{id:N}.json");
            string markerPath = Path.Combine(sessionDirectory, $"{id:N}.session");
            Assert.True(File.Exists(markerPath));
            File.Delete(sessionPath);
            await using (var missingSession = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
            {
                HostedProfileStatus missingState = Assert.Single(missingSession.GetProfiles());
                Assert.False(missingState.AutomaticActionsAllowed);
                Assert.False(File.Exists(sessionPath));
                Assert.True(File.Exists(markerPath));
            }
            string budgetPath = Path.Combine(root, "Budgets", $"{id:N}.json");
            File.Delete(budgetPath);

            await using var reopened = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            HostedProfileStatus blocked = Assert.Single(reopened.GetProfiles());
            Assert.False(blocked.AutomaticActionsAllowed);
            Assert.Contains("budget", blocked.Problem, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(budgetPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        string migrationRoot = Path.Combine(Path.GetTempPath(),
            $"relight-host-pending-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(migrationRoot);
        try
        {
            Guid id;
            await using (var legacy = await RecoveryApplicationHost.OpenAsync(migrationRoot,
                             new FakeClock()))
                id = await legacy.RegisterExecutableAsync("Disposable target", TestExecutable());
            var old = new RecoveryStateStore(migrationRoot);
            var budgets = new SharedRecoveryBudgetStore(migrationRoot);
            budgets.Create(id);
            budgets.BeginEpisode(id, 1, Guid.NewGuid());
            var session = new RecoverySessionStateStore(migrationRoot,
                WindowsLogonSessionIdentity.Current().StorageKey, budgets);
            Assert.Throws<InvalidOperationException>(() =>
                old.MigrateToSession(id, budgets, session));

            await using var reopened = await RecoveryApplicationHost.OpenSharedSessionAsync(
                migrationRoot, new FakeClock(), allowLegacyMigration: true);
            HostedProfileStatus blocked = Assert.Single(reopened.GetProfiles());
            Assert.False(blocked.AutomaticActionsAllowed);
            Assert.Contains("differs", blocked.Problem, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(LegacyStateOwnership.MigrationPending, old.GetOwnership(id));
            Assert.False(session.HasStateEvidence(id));
        }
        finally
        {
            if (Directory.Exists(migrationRoot)) Directory.Delete(migrationRoot, recursive: true);
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
    public async Task Notification_preferences_persist_without_changing_recovery_budget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-notify-prefs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var host = await RecoveryApplicationHost.OpenAsync(root,
                             new FakeClock()))
            {
                id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
                RecoveryPolicy policy = host.GetProfileForEdit(id).Policy;
                await host.UpdateProfileSettingsAsync(id, "Disposable target", policy,
                    notifyOnRecovery: false, notifyOnLockout: false);
                ProfileConfiguration edited = host.GetProfileForEdit(id);
                Assert.False(edited.NotifyOnRecovery);
                Assert.False(edited.NotifyOnLockout);
                Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                    .Checkpoint.ReservedAutomaticAttempts);
            }

            await using var reopened = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            ProfileConfiguration saved = reopened.GetProfileForEdit(id);
            Assert.False(saved.NotifyOnRecovery);
            Assert.False(saved.NotifyOnLockout);
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

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Host_restart_closes_and_relaunches_only_the_disposable_selected_target()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "target.ready");
        string label = $"host-restart-{Guid.NewGuid():N}";
        int? firstPid = null, secondPid = null;
        try
        {
            Guid id = Guid.NewGuid();
            var policy = RecoveryPolicy.Default;
            string[] arguments = ["--label", label, "--ready-file", ready,
                "--exit-after-ms", "30000"];
            var target = new ExecutableTarget(TestExecutable(), arguments,
                RequiredArgument: label);
            new ConfigurationStore(root).Initialize(new(
                [new(id, "Restartable disposable target", true,
                    new(TargetKind.Executable, target.CanonicalPath, [.. arguments],
                        RequiredArgument: label), policy)], GlobalConfiguration.Default));
            new RecoveryStateStore(root).Create(id,
                new RecoveryMachine(policy).ExportCheckpoint());
            await new ExecutableLauncher(target).LaunchAsync(Guid.NewGuid(),
                CancellationToken.None);
            firstPid = int.Parse((await WaitForFile(ready)).Split('|')[0]);

            await using var host = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            StopCommandResult stopped = await host.StopProfileForRestartAsync(id,
                TimeSpan.FromSeconds(3));
            Assert.Equal(TargetStopOutcome.Stopped, stopped.Stop.Outcome);
            Assert.True(new RecoveryStateStore(root).Load(id).Checkpoint.Paused);
            File.Delete(ready);

            CoordinatorResult restarted = await host.CompleteProfileRestartAsync(id,
                stopped.OperationId, stopped.SelectedIdentity);
            Assert.True(restarted.LaunchDispatched);
            secondPid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            Assert.NotEqual(firstPid, secondPid);
            Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                .Checkpoint.ReservedAutomaticAttempts);
            using Process running = Process.GetProcessById(secondPid.Value);
            Assert.False(running.HasExited);
        }
        finally
        {
            foreach (int? pid in new[] { firstPid, secondPid })
            {
                if (pid is null) continue;
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
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { return await File.ReadAllTextAsync(path, timeout.Token); }
                catch (IOException) { /* The target is still writing its readiness record. */ }
            }
            await Task.Delay(50, timeout.Token);
        }
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
