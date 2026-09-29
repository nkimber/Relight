using System.Diagnostics;
using Relight.Core;
using Relight.Engine;
using Relight.Storage;
using Relight.ViewModels;
using Relight.Windows;

namespace Relight.Core.Tests;

[Collection("Windows desktop process tests")]
public sealed class RecoveryApplicationHostIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Verified_exit_callback_and_manual_start_share_one_launch_gate()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"relight-exit-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "target.ready");
        string label = $"exit-race-{Guid.NewGuid():N}";
        Process? initial = null;
        int? launchedPid = null;
        try
        {
            string[] arguments = ["--label", label, "--ready-file", ready,
                "--exit-after-ms", "30000"];
            var start = new ProcessStartInfo(TestExecutable()) { UseShellExecute = false };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            initial = Process.Start(start) ??
                throw new InvalidOperationException("Disposable target did not start.");
            await WaitForFile(ready);
            Guid id = Guid.NewGuid();
            RecoveryPolicy policy = RecoveryPolicy.Default with
            {
                NormalPollInterval = TimeSpan.FromSeconds(60),
                ObservationPeriod = TimeSpan.FromSeconds(60),
                RetryDelay = TimeSpan.FromSeconds(5)
            };
            new ConfigurationStore(root).Initialize(new(
                [new(id, "Exit race target", true,
                    new(TargetKind.Executable, TestExecutable(), [.. arguments],
                        RequiredArgument: label), policy)], GlobalConfiguration.Default));
            new RecoveryStateStore(root).Create(id,
                new RecoveryMachine(policy).ExportCheckpoint());
            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
                clock);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            for (int second = 5; second <= 65; second += 5)
            {
                clock.Elapsed = TimeSpan.FromSeconds(second);
                await Task.WhenAll(host.Pulse().Values).WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(RecoveryState.Healthy,
                Assert.Single(host.GetProfiles()).Recovery?.State);
            Assert.NotNull(Assert.Single(host.GetProfiles()).Recovery?.TargetIdentity);

            initial.Kill(entireProcessTree: false);
            await initial.WaitForExitAsync();
            File.Delete(ready);
            // The 60-second normal poll is not due again. Only the real Exited
            // subscription can make this pulse available at the current clock.
            Task? callbackPoll = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (callbackPoll is null)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    host.Pulse().TryGetValue(id, out callbackPoll);
                    if (callbackPoll is null)
                        await Task.Delay(20, timeout.Token);
                }
            }
            await callbackPoll.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Elapsed = TimeSpan.FromSeconds(67);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecoveryState.RetryWaiting,
                Assert.Single(host.GetProfiles()).Recovery?.State);

            clock.Elapsed = TimeSpan.FromSeconds(72);
            Task<CoordinatorResult> explicitStart = host.StartProfileNowAsync(id);
            Task duePoll = Assert.Single(host.Pulse()).Value;
            await Task.WhenAll(explicitStart, duePoll).WaitAsync(TimeSpan.FromSeconds(10));
            string[] identity = (await WaitForFile(ready)).Split('|');
            launchedPid = int.Parse(identity[0]);
            Assert.NotEqual(initial.Id, launchedPid);
            Assert.Equal(1, new RecoveryStateStore(root).Load(id).Checkpoint
                .ReservedAutomaticAttempts + ((await explicitStart).LaunchDispatched ? 1 : 0));
            var history = new OperationalEventHistoryReader(root);
            EventHistoryResult automatic = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: id, Kind: OperationalEventKind.LaunchDispatched));
            EventHistoryResult manual = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: id, Kind: OperationalEventKind.ExplicitStartDispatched));
            Assert.Equal(1, automatic.TotalMatches + manual.TotalMatches);
            if (automatic.TotalMatches == 1)
            {
                OperationalEvent reserved = Assert.Single((await history.ReadAsync(
                    new EventHistoryQuery(ProfileId: id,
                        Kind: OperationalEventKind.LaunchReserved))).Events);
                Assert.Equal(reserved.OperationId, Assert.Single(automatic.Events).OperationId);
            }
        }
        finally
        {
            foreach (int? pid in new int?[] { launchedPid, initial?.Id })
            {
                if (pid is null) continue;
                try
                {
                    using Process process = Process.GetProcessById(pid.Value);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: false);
                        await process.WaitForExitAsync();
                    }
                }
                catch (ArgumentException) { }
            }
            initial?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Ambiguous_live_instances_show_candidates_and_suspend_actions()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"relight-ambiguous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string label = $"ambiguous-{Guid.NewGuid():N}";
        Process? first = null, second = null;
        try
        {
            Guid id;
            await using (var setup = await RecoveryApplicationHost.OpenSharedSessionAsync(root,
                             new FakeClock()))
                id = await setup.RegisterExecutableAsync("Ambiguous disposable target",
                    TestExecutable(), ["--label", label, "--hidden"], null,
                    RecoveryPolicy.Default with
                    {
                        StartAutomaticallyWhenInitiallyAbsent = true
                    });
            var store = new ConfigurationStore(root);
            StoredConfiguration current = store.Load();
            ProfileConfiguration profile = Assert.Single(current.Configuration.Profiles);
            store.Save(current, current.Configuration with
            {
                Profiles = [profile with { Target = profile.Target with
                {
                    RequiredArgument = label
                } }]
            });
            Process Start(string ready)
            {
                var info = new ProcessStartInfo(TestExecutable())
                {
                    UseShellExecute = false
                };
                foreach (string argument in new[] { "--label", label, "--hidden",
                    "--ready-file", ready, "--exit-after-ms", "60000" })
                    info.ArgumentList.Add(argument);
                return Process.Start(info) ??
                    throw new InvalidOperationException("Disposable target did not start.");
            }
            first = Start(Path.Combine(root, "first.ready"));
            second = Start(Path.Combine(root, "second.ready"));
            await WaitForFile(Path.Combine(root, "first.ready"));
            await WaitForFile(Path.Combine(root, "second.ready"));

            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(root,
                clock);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            HostedProfileStatus status = Assert.Single(host.GetProfiles());
            Assert.True(status.Recovery?.DetectionUnavailable);
            Assert.Equal(DetectionFailureKind.Ambiguous, status.Detection?.FailureKind);
            Assert.Contains($"PID {first.Id}", status.Detection?.Reason);
            Assert.Contains($"PID {second.Id}", status.Detection?.Reason);
            var dashboard = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
            dashboard.UpdateMonitoring(null, false, [status], null, clock.Elapsed);
            ApplicationStatusRow row = Assert.Single(dashboard.ApplicationRows);
            Assert.Equal("Detection unavailable", row.State);
            Assert.Contains($"PID {first.Id}", row.Detail);
            Assert.Contains($"PID {second.Id}", row.Detail);
            Assert.Contains("Automatic actions are suspended", row.Detail);
            Assert.False(row.CanStartNow);
            Assert.False(row.CanStopAndPause);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.StartProfileNowAsync(id));
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);
            Assert.False(first.HasExited);
            Assert.False(second.HasExited);
            EventHistoryResult events = await new OperationalEventHistoryReader(root)
                .ReadAsync(new EventHistoryQuery(ProfileId: id, Limit: 100));
            Assert.Contains(events.Events, entry =>
                entry.Kind == OperationalEventKind.DetectionUnavailable &&
                entry.FailureCategory == OperationalFailureCategory.DetectionAmbiguous);
            Assert.DoesNotContain(events.Events, entry =>
                entry.Kind is OperationalEventKind.LaunchReserved or
                    OperationalEventKind.LaunchDispatched or
                    OperationalEventKind.ExplicitStartDispatched);
        }
        finally
        {
            foreach (Process? process in new[] { first, second })
            {
                if (process is null) continue;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync();
                }
                process.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Windows_interruption_marks_profiles_unknown_then_reconciles_immediately()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"relight-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(
                root, clock);
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
            await Task.WhenAll(host.Pulse().Values);
            clock.Elapsed = TimeSpan.FromSeconds(1);
            Assert.Empty(host.Pulse());

            await host.ReconcileAfterWindowsInterruptionAsync();
            Assert.True(host.GetProfiles().Single(profile => profile.Id == id)
                .Recovery?.DetectionUnavailable);
            await Assert.Single(host.Pulse()).Value;
            Assert.False(host.GetProfiles().Single(profile => profile.Id == id)
                .Recovery?.DetectionUnavailable);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock());
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
            await using (var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
            await using var reopened = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
            await using (var configured = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
            await using (var degraded = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
                new FakeClock()))
            {
                Assert.True(degraded.Configuration?.FromLastGoodBackup);
                Assert.NotNull(degraded.ConfigurationProblem);
                archived = await degraded.RepairConfigurationAsync();
            }
            Assert.NotNull(archived);
            Assert.Equal("{ invalid external edit", File.ReadAllText(archived));
            await using var reopened = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
            await using (var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock()))
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

            await using var reopened = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock());
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
    public async Task Registration_preserves_selected_initial_start_policy()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-initial-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            RecoveryPolicy selected = RecoveryPolicy.Default with
            {
                StartAutomaticallyWhenInitiallyAbsent = true
            };
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(
                root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Initial-start target",
                TestExecutable(), [], root, selected);

            ProfileConfiguration saved = Assert.Single(
                new ConfigurationStore(root).Load().Configuration.Profiles);
            Assert.Equal(id, saved.Id);
            Assert.Equal(selected, saved.Policy);
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
    public async Task Test_launch_reports_discovered_target_and_adopts_it_on_repeat()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-test-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "target.ready");
        int? pid = null;
        try
        {
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(
                root, new FakeClock());
            Guid id = await host.RegisterExecutableAsync("Test target", TestExecutable(),
                ["--ready-file", ready, "--exit-after-ms", "30000"],
                Path.GetTempPath());

            TestLaunchReport first = await host.TestProfileLaunchAsync(id)
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(first.LaunchDispatched);
            Assert.Equal(DetectionKind.Present, first.Detection.Kind);
            pid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            Assert.Contains($"{pid}", first.Detection.Identity);
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));

            TestLaunchReport second = await host.TestProfileLaunchAsync(id)
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(second.LaunchDispatched);
            Assert.Equal(first.Detection.Identity, second.Detection.Identity);
            Assert.Equal(0, new RecoveryStateStore(root).Load(id)
                .Checkpoint.ReservedAutomaticAttempts);
        }
        finally
        {
            if (pid is null && File.Exists(ready))
            {
                try { pid = int.Parse((await File.ReadAllTextAsync(ready)).Split('|')[0]); }
                catch (Exception error) when (error is IOException or FormatException) { }
            }
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
    public async Task Shared_host_explicitly_repairs_untrusted_session_state_without_resetting_budget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-host-state-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid id;
            await using (var created = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
                id = await created.RegisterExecutableAsync("Disposable target", TestExecutable());
            var budgets = new SharedRecoveryBudgetStore(root);
            SharedRecoveryBudget before = budgets.Load(id);
            string path = Path.Combine(root, "Sessions",
                WindowsLogonSessionIdentity.Current().StorageKey, "State", $"{id:N}.json");
            File.WriteAllText(path, "untrusted session bytes");

            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            HostedProfileStatus unavailable = Assert.Single(host.GetProfiles());
            Assert.False(unavailable.AutomaticActionsAllowed);
            Assert.True(unavailable.CanRepairRecoveryState);
            Assert.Equal(0, budgets.Load(id).ReservedAutomaticAttempts);

            await host.RepairUnavailableRecoveryStateAsync(id);

            HostedProfileStatus repaired = Assert.Single(host.GetProfiles());
            Assert.True(repaired.AutomaticActionsAllowed, repaired.Problem);
            Assert.False(repaired.CanRepairRecoveryState);
            Assert.True(repaired.Recovery?.Paused);
            Assert.Equal(before, budgets.Load(id));
            string evidence = Path.Combine(root, "Sessions",
                WindowsLogonSessionIdentity.Current().StorageKey, "State",
                "RepairEvidence", id.ToString("N"));
            string saved = Assert.Single(Directory.GetFiles(evidence,
                "state.json", SearchOption.AllDirectories));
            Assert.Equal("untrusted session bytes", File.ReadAllText(saved));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "WindowsDesktop")]
    public async Task Shared_host_replaces_untrusted_budget_with_distinct_disabled_profile(
        bool corruptBudget)
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-budget-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid originalId;
            await using (var created = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
                originalId = await created.RegisterExecutableAsync("Disposable target", TestExecutable());
            string originalBudget = Path.Combine(root, "Budgets", $"{originalId:N}.json");
            string originalSession = Path.Combine(root, "Sessions",
                WindowsLogonSessionIdentity.Current().StorageKey, "State",
                $"{originalId:N}.json");
            byte[] sessionBytes = File.ReadAllBytes(originalSession);
            if (corruptBudget) File.WriteAllText(originalBudget, "untrusted budget bytes");
            else File.Delete(originalBudget);

            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
                root, new FakeClock());
            HostedProfileStatus unavailable = Assert.Single(host.GetProfiles());
            Assert.False(unavailable.AutomaticActionsAllowed);
            Assert.False(unavailable.CanRepairRecoveryState);
            Assert.True(unavailable.CanReplaceUnavailableProfile);

            Guid replacementId = await host.ReplaceUnavailableProfileAsync(originalId);

            Assert.NotEqual(originalId, replacementId);
            HostedProfileStatus replacement = Assert.Single(host.GetProfiles());
            Assert.Equal(replacementId, replacement.Id);
            Assert.False(replacement.ConfiguredEnabled);
            Assert.False(replacement.Recovery?.Enabled);
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(replacementId)
                .ReservedAutomaticAttempts);
            Assert.Equal(sessionBytes, File.ReadAllBytes(originalSession));
            Assert.Equal(corruptBudget, File.Exists(originalBudget));
            if (corruptBudget)
                Assert.Equal("untrusted budget bytes", File.ReadAllText(originalBudget));
            Assert.Equal(replacementId,
                Assert.Single(new ConfigurationStore(root).Load().Configuration.Profiles).Id);
            Assert.False(Assert.Single(new ConfigurationStore(root).Load()
                .Configuration.Profiles).Enabled);
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
                string configurationPath = Path.Combine(root, "configuration.json");
                using (var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    while (true)
                    {
                        writeTimeout.Token.ThrowIfCancellationRequested();
                        try
                        {
                            File.WriteAllText(configurationPath, "invalid configuration");
                            break;
                        }
                        catch (IOException)
                        {
                            // The watcher may briefly hold its read lock while an editor writes.
                            await Task.Delay(50, writeTimeout.Token);
                        }
                    }
                }
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
    public async Task Ordinary_host_creates_shared_budget_and_session_state_for_new_profile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-production-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Guid profileId;
            await using (var host = await RecoveryApplicationHost.OpenAsync(root,
                             new FakeClock()))
                profileId = await host.RegisterExecutableAsync("Disposable target",
                    TestExecutable());

            SharedRecoveryBudget budget = new SharedRecoveryBudgetStore(root).Load(profileId);
            var session = new RecoverySessionStateStore(root,
                WindowsLogonSessionIdentity.Current().StorageKey,
                new SharedRecoveryBudgetStore(root));
            Assert.Equal(0, budget.ReservedAutomaticAttempts);
            Assert.True(session.Load(profileId).Checkpoint.Enabled);
            Assert.False(File.Exists(Path.Combine(root, "State", $"{profileId:N}.json")));

            await using var reopened = await RecoveryApplicationHost.OpenAsync(root,
                new FakeClock());
            Assert.True(Assert.Single(reopened.GetProfiles()).AutomaticActionsAllowed);
            Assert.Equal(budget, new SharedRecoveryBudgetStore(root).Load(profileId));
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
            await using (var legacy = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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

            await using (var migrated = await RecoveryApplicationHost.OpenAsync(
                             root, new FakeClock()))
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

            await using var reopened = await RecoveryApplicationHost.OpenAsync(
                root, new FakeClock());
            Assert.True(Assert.Single(reopened.GetProfiles()).AutomaticActionsAllowed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "WindowsDesktop")]
    public async Task Invalid_legacy_state_suspends_ordinary_startup_without_losing_original_bytes(
        bool corruptConfiguration)
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"relight-invalid-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "target.ready");
        try
        {
            string label = $"invalid-legacy-{Guid.NewGuid():N}";
            RecoveryPolicy policy = RecoveryPolicy.Default with
            {
                StartAutomaticallyWhenInitiallyAbsent = true,
                RetryDelay = TimeSpan.FromSeconds(5)
            };
            Guid id;
            await using (var legacy = await RecoveryApplicationHost.OpenLegacyForTestsAsync(
                             root, new FakeClock()))
                id = await legacy.RegisterExecutableAsync("Disposable target", TestExecutable(),
                    ["--label", label, "--ready-file", ready, "--hidden"],
                    Path.GetTempPath(), policy);

            var configurations = new ConfigurationStore(root);
            StoredConfiguration current = configurations.Load();
            configurations.Save(current, current.Configuration);
            string statePath = Path.Combine(root, "State", $"{id:N}.json");
            const string invalidState = "untrusted legacy state bytes";
            File.WriteAllText(statePath, invalidState);
            string configurationPath = Path.Combine(root, "configuration.json");
            const string invalidConfiguration = "untrusted configuration bytes";
            if (corruptConfiguration)
                File.WriteAllText(configurationPath, invalidConfiguration);

            var clock = new FakeClock();
            await using (var reopened = await RecoveryApplicationHost.OpenAsync(root, clock))
            {
                HostedProfileStatus blocked = Assert.Single(reopened.GetProfiles());
                Assert.Equal(id, blocked.Id);
                Assert.False(blocked.AutomaticActionsAllowed);
                Assert.NotNull(blocked.Problem);
                if (corruptConfiguration)
                    Assert.NotNull(reopened.ConfigurationProblem);
                clock.Elapsed = TimeSpan.FromMinutes(5);
                foreach (Task pulse in reopened.Pulse().Values)
                    await pulse.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(Assert.Single(reopened.GetProfiles()).AutomaticActionsAllowed);
            }

            Assert.Equal(invalidState, File.ReadAllText(statePath));
            Assert.False(File.Exists(Path.Combine(root, "State", $"{id:N}.legacy.json")));
            Assert.False(new SharedRecoveryBudgetStore(root).HasBudgetEvidence(id));
            Assert.False(File.Exists(ready));
            if (corruptConfiguration)
                Assert.Equal(invalidConfiguration, File.ReadAllText(configurationPath));
            Assert.Equal(0, (await new OperationalEventHistoryReader(root).ReadAsync(
                new EventHistoryQuery(ProfileId: id,
                    Kind: OperationalEventKind.LaunchDispatched))).TotalMatches);
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
            await using (var legacy = await RecoveryApplicationHost.OpenLegacyForTestsAsync(migrationRoot,
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

            await using var reopened = await RecoveryApplicationHost.OpenAsync(
                migrationRoot, new FakeClock());
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
    public async Task Corrupt_shared_budget_suspends_launch_but_keeps_external_discovery_live()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-passive-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "external.ready");
        Process? external = null;
        try
        {
            string label = $"passive-{Guid.NewGuid():N}";
            RecoveryPolicy policy = RecoveryPolicy.Default with
            {
                StartAutomaticallyWhenInitiallyAbsent = true
            };
            Guid id;
            await using (var created = await RecoveryApplicationHost.OpenSharedSessionAsync(
                             root, new FakeClock()))
                id = await created.RegisterExecutableAsync("Disposable target", TestExecutable(),
                    ["--label", label, "--hidden", "--ready-file", ready,
                        "--exit-after-ms", "30000"], Path.GetTempPath(), policy);
            string budgetPath = Path.Combine(root, "Budgets", $"{id:N}.json");
            const string untrusted = "corrupt budget bytes must be preserved";
            File.WriteAllText(budgetPath, untrusted);

            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(root, clock);
            HostedProfileStatus degraded = Assert.Single(host.GetProfiles());
            Assert.True(degraded.Monitoring);
            Assert.False(degraded.AutomaticActionsAllowed);
            Assert.NotNull(degraded.Problem);
            Assert.Equal(DetectionKind.Absent, degraded.Detection?.Kind);

            var start = new ProcessStartInfo(TestExecutable())
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "--label", label, "--hidden",
                "--ready-file", ready, "--exit-after-ms", "30000" })
                start.ArgumentList.Add(argument);
            external = Process.Start(start) ??
                throw new InvalidOperationException("Disposable target did not start.");
            await WaitForFile(ready);

            clock.Elapsed = policy.LockoutDiscoveryInterval;
            await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            HostedProfileStatus observed = Assert.Single(host.GetProfiles());
            Assert.Equal(DetectionKind.Present, observed.Detection?.Kind);
            Assert.False(observed.AutomaticActionsAllowed);
            Assert.NotNull(observed.Problem);
            var dashboard = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
            dashboard.UpdateMonitoring(null, false, [observed], null, clock.Elapsed);
            ApplicationStatusRow row = Assert.Single(dashboard.ApplicationRows);
            Assert.Equal(ApplicationStatusCategory.Attention, row.Category);
            Assert.Equal("Detection only", row.State);
            Assert.Contains("Recovery state is unavailable", row.Detail);
            Assert.False(row.CanStartNow);
            Assert.False(row.CanStopAndPause);
            Assert.Equal(untrusted, File.ReadAllText(budgetPath));
            Assert.False(external.HasExited);
            EventHistoryResult dispatched = await new OperationalEventHistoryReader(root)
                .ReadAsync(new EventHistoryQuery(ProfileId: id,
                    Kind: OperationalEventKind.LaunchDispatched));
            Assert.Equal(0, dispatched.TotalMatches);
        }
        finally
        {
            if (external is not null)
            {
                if (!external.HasExited)
                {
                    external.Kill(entireProcessTree: false);
                    await external.WaitForExitAsync();
                }
                external.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Unwritable_log_path_is_visible_without_blocking_automatic_recovery()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-log-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Logs"), "blocked log directory");
        string ready = Path.Combine(root, "target.ready");
        int? pid = null;
        try
        {
            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(root, clock);
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable(),
                ["--ready-file", ready, "--exit-after-ms", "30000"], Path.GetTempPath(),
                RecoveryPolicy.Default with { StartAutomaticallyWhenInitiallyAbsent = true });
            foreach (int second in new[] { 0, 2, 32 })
            {
                clock.Elapsed = TimeSpan.FromSeconds(second);
                await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            }

            pid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            Assert.Equal(1, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);
            EventRecorderStatus? logging = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (logging?.Degraded != true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                logging = await host.GetLoggingStatusAsync(timeout.Token);
                if (logging?.Degraded != true) await Task.Delay(20, timeout.Token);
            }
            Assert.True(logging.Journal.BufferedCount > 0);
            var dashboard = new ShellViewModel(() => { }, () => { }, () => { }, () => { });
            dashboard.UpdateMonitoring(null, false, host.GetProfiles(), logging, clock.Elapsed);
            Assert.Contains("Event logging is degraded", dashboard.MonitoringBanner);
            using (Process target = Process.GetProcessById(pid.Value))
                Assert.False(target.HasExited);
            Assert.Equal("blocked log directory", File.ReadAllText(Path.Combine(root, "Logs")));
        }
        finally
        {
            if (pid is { } running)
            {
                try
                {
                    using Process target = Process.GetProcessById(running);
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: false);
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
    public async Task Inaccessible_shared_budget_lease_prevents_due_launch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"relight-budget-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string ready = Path.Combine(root, "unexpected.ready");
        try
        {
            var clock = new FakeClock();
            await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(root, clock);
            Guid id = await host.RegisterExecutableAsync("Disposable target", TestExecutable(),
                ["--ready-file", ready, "--exit-after-ms", "30000"], Path.GetTempPath(),
                RecoveryPolicy.Default with { StartAutomaticallyWhenInitiallyAbsent = true });
            foreach (int second in new[] { 0, 2 })
            {
                clock.Elapsed = TimeSpan.FromSeconds(second);
                await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(5));
            }

            string leasePath = Path.Combine(root, "Budgets", $"{id:N}.json.lock");
            using (var blocked = new FileStream(leasePath, FileMode.OpenOrCreate,
                       FileAccess.ReadWrite, FileShare.None))
            {
                clock.Elapsed = TimeSpan.FromSeconds(32);
                await Assert.Single(host.Pulse()).Value.WaitAsync(TimeSpan.FromSeconds(8));
            }

            Assert.False(File.Exists(ready));
            Assert.Equal(0, new SharedRecoveryBudgetStore(root).Load(id)
                .ReservedAutomaticAttempts);
            HostedProfileStatus degraded = Assert.Single(host.GetProfiles());
            Assert.False(degraded.AutomaticActionsAllowed);
            Assert.NotNull(degraded.Problem);
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
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock());
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
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock());
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
            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock());
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
            await using (var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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

            await using var reopened = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
            await using (var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, new FakeClock()))
            {
                id = await host.RegisterExecutableAsync("Disposable target", TestExecutable());
                await host.SetProfileEnabledAsync(id, false);
                Assert.False(Assert.Single(host.GetProfiles()).ConfiguredEnabled);
            }

            await using (var reopened = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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

            await using (var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root, clock))
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

            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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

            await using var host = await RecoveryApplicationHost.OpenLegacyForTestsAsync(root,
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
