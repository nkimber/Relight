using System.Diagnostics;
using System.Globalization;
using System.Management;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

[Collection("Windows desktop process tests")]
public sealed class RecoveryWpfPreviewProcessTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Launcher_exit_is_not_counted_as_failure_when_child_is_selected_target()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-handoff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-handoff-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "target.ready");
        string attemptsFile = Path.Combine(directory, "attempts.bin");
        Process? preview = null;
        int? childPid = null;
        long? childStartedTicks = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--handoff", "--ready-file", ready,
                    "--attempt-file", attemptsFile, "--exit-after-ms", "180000"],
                maximumAttempts: 3, normalPollInterval: TimeSpan.FromSeconds(60));
            var store = new ConfigurationStore(directory);
            StoredConfiguration current = store.Load();
            ProfileConfiguration saved = Assert.Single(current.Configuration.Profiles);
            store.Save(current, current.Configuration with
            {
                Profiles = [saved with { Target = saved.Target with
                {
                    RequiredArgument = label,
                    ExcludedArgument = "--handoff"
                } }]
            });
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);

            await WaitUntilAsync(() => File.Exists(ready) && File.Exists(attemptsFile),
                TimeSpan.FromSeconds(25));
            string[] identity = (await File.ReadAllTextAsync(ready)).Split('|');
            childPid = int.Parse(identity[0], CultureInfo.InvariantCulture);
            childStartedTicks = DateTimeOffset.Parse(identity[1],
                CultureInfo.InvariantCulture).UtcTicks;
            Assert.True(IsSameTargetAlive(childPid.Value, childStartedTicks.Value,
                targetExecutable));
            Assert.Equal(1, BitConverter.ToInt32(File.ReadAllBytes(attemptsFile)));

            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationCompleted, 1,
                TimeSpan.FromSeconds(75));
            Assert.False(preview.HasExited);
            Assert.True(IsSameTargetAlive(childPid.Value, childStartedTicks.Value,
                targetExecutable));
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);
            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Limit: 100));
            OperationalEvent reserved = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.LaunchReserved);
            OperationalEvent dispatched = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.LaunchDispatched);
            Assert.Equal(reserved.OperationId, dispatched.OperationId);
            Assert.DoesNotContain(events.Events, entry =>
                entry.Kind is OperationalEventKind.TargetDisappeared or
                    OperationalEventKind.ObservationInterrupted or
                    OperationalEventKind.LaunchFailed);
            OperationalEvent[] observed = events.Events.Where(entry =>
                entry.Kind == OperationalEventKind.TargetObserved).ToArray();
            Assert.NotEmpty(observed);
            Assert.All(observed, entry =>
                Assert.Equal(childPid.Value.ToString(CultureInfo.InvariantCulture),
                    entry.ProcessIdentity?.Split('|').ElementAtOrDefault(2)));
        }
        finally
        {
            StopStartedProcess(preview);
            StopLabeledTestTargets(targetExecutable, label);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Surviving_helper_does_not_mask_main_target_exit()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-helper-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "target.ready");
        string[] arguments = ["--label", label, "--spawn-helper", "--hidden",
            "--ready-file", ready, "--exit-after-ms", "20000",
            "--helper-ms", "120000"];
        Process? preview = null, firstMain = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                arguments, maximumAttempts: 1, initialAutomaticStart: false);
            var store = new ConfigurationStore(directory);
            StoredConfiguration current = store.Load();
            ProfileConfiguration saved = Assert.Single(current.Configuration.Profiles);
            store.Save(current, current.Configuration with
            {
                Profiles = [saved with { Target = saved.Target with
                {
                    RequiredArgument = label,
                    ExcludedArgument = "--helper"
                } }]
            });
            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            firstMain = Process.Start(start) ??
                throw new InvalidOperationException("Initial main target did not start.");
            await WaitUntilAsync(() => File.Exists(ready) && File.Exists(ready + ".helper"),
                TimeSpan.FromSeconds(8));
            string[] helperIdentity = (await File.ReadAllTextAsync(ready + ".helper"))
                .Split('|');
            int helperPid = int.Parse(helperIdentity[0], CultureInfo.InvariantCulture);
            long helperStarted = DateTimeOffset.Parse(helperIdentity[1],
                CultureInfo.InvariantCulture).UtcTicks;
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForTargetObservationsAsync(history, profileId, 1,
                TimeSpan.FromSeconds(10));
            await firstMain.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.True(IsSameTargetAlive(helperPid, helperStarted, targetExecutable));
            File.Delete(ready);

            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.TargetDisappeared, 1, TimeSpan.FromSeconds(12));
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.LaunchDispatched, 1, TimeSpan.FromSeconds(20));
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(10));
            int replacementPid = int.Parse((await File.ReadAllTextAsync(ready)).Split('|')[0],
                CultureInfo.InvariantCulture);
            Assert.NotEqual(firstMain.Id, replacementPid);
            Assert.NotEqual(helperPid, replacementPid);
            Assert.True(IsSameTargetAlive(helperPid, helperStarted, targetExecutable));
            Assert.Equal(1, new SharedRecoveryBudgetStore(directory).Load(profileId)
                .ReservedAutomaticAttempts);
            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Limit: 100));
            Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.LaunchDispatched);
            Assert.DoesNotContain(events.Events, entry =>
                entry.Kind == OperationalEventKind.TargetObserved &&
                entry.ProcessIdentity?.Split('|').ElementAtOrDefault(2) ==
                    helperPid.ToString(CultureInfo.InvariantCulture));
            Assert.False(preview.HasExited);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(firstMain);
            StopLabeledTestTargets(targetExecutable, label);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Repeated_dashboard_close_and_second_launch_keep_one_tray_process()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-window-cycles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string executable = FindBuiltExecutable("Relight.App", "Relight.exe");
        Process? primary = null;
        try
        {
            primary = StartPreview(executable, directory);
            await WaitUntilAsync(() => File.Exists(Path.Combine(directory,
                "configuration.json")), TimeSpan.FromSeconds(10));
            for (int cycle = 0; cycle < 5; cycle++)
            {
                using Process secondary = StartPreview(executable, directory);
                await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, secondary.ExitCode);
                await WaitUntilAsync(() =>
                {
                    primary.Refresh();
                    return primary.MainWindowHandle != nint.Zero;
                }, TimeSpan.FromSeconds(10));
                Assert.True(primary.CloseMainWindow());
                await WaitUntilAsync(() =>
                {
                    primary.Refresh();
                    return primary.MainWindowHandle == nint.Zero;
                }, TimeSpan.FromSeconds(10));
                Assert.False(primary.HasExited);
            }
        }
        finally
        {
            if (primary is not null)
            {
                if (!primary.HasExited)
                {
                    primary.Kill(entireProcessTree: false);
                    await primary.WaitForExitAsync();
                }
                primary.Dispose();
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Distinct_preview_directories_keep_separate_closed_tray_instances()
    {
        string root = Path.Combine(Path.GetTempPath(),
            $"relight-preview-isolation-{Guid.NewGuid():N}");
        string firstDirectory = Path.Combine(root, "first");
        string secondDirectory = Path.Combine(root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string executable = FindBuiltExecutable("Relight.App", "Relight.exe");
        Process? first = null, second = null;
        try
        {
            first = StartPreview(executable, firstDirectory);
            await WaitUntilAsync(() => File.Exists(Path.Combine(firstDirectory,
                "configuration.json")), TimeSpan.FromSeconds(10));
            second = StartPreview(executable, secondDirectory);
            await WaitUntilAsync(() => File.Exists(Path.Combine(secondDirectory,
                "configuration.json")), TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            first.Refresh();
            second.Refresh();
            Assert.False(first.HasExited);
            Assert.False(second.HasExited);
            Assert.Equal(nint.Zero, first.MainWindowHandle);
            Assert.Equal(nint.Zero, second.MainWindowHandle);
        }
        finally
        {
            foreach (Process? preview in new[] { first, second })
            {
                if (preview is null) continue;
                if (!preview.HasExited)
                {
                    preview.Kill(entireProcessTree: false);
                    await preview.WaitForExitAsync();
                }
                preview.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Preview_crash_leaves_disposable_target_running_and_charged_budget_intact()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string ready = Path.Combine(directory, "target.ready");
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-restart-{Guid.NewGuid():N}";
        Process? first = null, restarted = null;
        int? targetPid = null;
        long? targetStartedTicks = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--ready-file", ready], maximumAttempts: 1);

            first = StartPreview(relightExecutable, directory);
            var budgets = new SharedRecoveryBudgetStore(directory);
            await WaitUntilAsync(() =>
            {
                if (first.HasExited)
                    throw new InvalidOperationException(
                        $"WPF preview exited before recovery: {first.ExitCode}.");
                return File.Exists(ready) &&
                    TryLoadBudget(budgets, profileId)?.ReservedAutomaticAttempts == 1;
            }, TimeSpan.FromSeconds(45));
            string[] identity = (await File.ReadAllTextAsync(ready)).Split('|');
            targetPid = int.Parse(identity[0], CultureInfo.InvariantCulture);
            targetStartedTicks = DateTimeOffset.Parse(identity[1],
                CultureInfo.InvariantCulture).UtcTicks;
            var history = new OperationalEventHistoryReader(directory);
            int observationsBeforeRestart = await WaitForTargetObservationsAsync(history,
                profileId, 1, TimeSpan.FromSeconds(10));
            ProfileConfiguration policyBefore = Assert.Single(
                new ConfigurationStore(directory).Load().Configuration.Profiles);
            Assert.Equal(profileId, policyBefore.Id);
            Assert.Equal(1, policyBefore.Policy.MaximumAutomaticAttempts);
            Assert.True(policyBefore.Policy.StartAutomaticallyWhenInitiallyAbsent);
            SharedRecoveryBudget budgetBefore = budgets.Load(profileId);
            Assert.NotNull(budgetBefore.EpisodeId);
            Assert.Equal(1, budgetBefore.ReservedAutomaticAttempts);
            string sessionDirectory = Assert.Single(Directory.GetDirectories(
                Path.Combine(directory, "Sessions")));
            var sessionStates = new RecoveryStateStore(sessionDirectory);
            RecoveryCheckpoint checkpointBefore = sessionStates.Load(profileId).Checkpoint;
            Assert.True(checkpointBefore.Enabled);
            Assert.False(checkpointBefore.Paused);
            Assert.Equal(1, checkpointBefore.ReservedAutomaticAttempts);
            Assert.Equal(budgetBefore.EpisodeId, checkpointBefore.EpisodeId);

            first.Kill(entireProcessTree: false);
            await first.WaitForExitAsync();
            Assert.True(IsSameTargetAlive(targetPid.Value, targetStartedTicks.Value,
                targetExecutable));
            Assert.Equal(1, budgets.Load(profileId).ReservedAutomaticAttempts);
            ProfileConfiguration afterCrash = Assert.Single(
                new ConfigurationStore(directory).Load().Configuration.Profiles);
            Assert.Equal(policyBefore.Id, afterCrash.Id);
            Assert.Equal(policyBefore.Policy, afterCrash.Policy);
            Assert.Equal(policyBefore.Target.Identity, afterCrash.Target.Identity);
            Assert.Equal(policyBefore.Target.Arguments, afterCrash.Target.Arguments);

            restarted = StartPreview(relightExecutable, directory);
            await WaitForTargetObservationsAsync(history, profileId,
                observationsBeforeRestart + 1, TimeSpan.FromSeconds(15));
            Assert.False(restarted.HasExited);
            Assert.True(IsSameTargetAlive(targetPid.Value, targetStartedTicks.Value,
                targetExecutable));
            SharedRecoveryBudget budgetAfter = budgets.Load(profileId);
            Assert.Equal(1, budgetAfter.ReservedAutomaticAttempts);
            Assert.Equal(budgetBefore.EpisodeId, budgetAfter.EpisodeId);
            RecoveryCheckpoint checkpointAfter = sessionStates.Load(profileId).Checkpoint;
            Assert.True(checkpointAfter.Enabled);
            Assert.False(checkpointAfter.Paused);
            Assert.Equal(1, checkpointAfter.ReservedAutomaticAttempts);
            Assert.Equal(checkpointBefore.EpisodeId, checkpointAfter.EpisodeId);
            ProfileConfiguration afterRestart = Assert.Single(
                new ConfigurationStore(directory).Load().Configuration.Profiles);
            Assert.Equal(policyBefore.Id, afterRestart.Id);
            Assert.Equal(policyBefore.Policy, afterRestart.Policy);
            Assert.Equal(policyBefore.Target.Identity, afterRestart.Target.Identity);
            Assert.Equal(policyBefore.Target.Arguments, afterRestart.Target.Arguments);
            EventHistoryResult observations = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved));
            Assert.Contains(observations.Events, entry =>
            {
                string[] fields = entry.ProcessIdentity?.Split('|') ?? [];
                return fields.Length == 4 &&
                    fields[2] == targetPid.Value.ToString(CultureInfo.InvariantCulture) &&
                    fields[3] == targetStartedTicks.Value.ToString(CultureInfo.InvariantCulture);
            });
            Assert.Equal(1, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);
            Detection found = await new ExecutableDiscovery(new ExecutableTarget(
                targetExecutable, ["--label", label], RequiredArgument: label))
                .DetectAsync(CancellationToken.None);
            Assert.Equal(DetectionKind.Present, found.Kind);
        }
        finally
        {
            StopStartedProcess(restarted);
            StopStartedProcess(first);
            if (targetPid is { } pid && targetStartedTicks is { } started &&
                IsSameTargetAlive(pid, started, targetExecutable))
            {
                using Process target = Process.GetProcessById(pid);
                target.Kill(entireProcessTree: false);
                target.WaitForExit(5000);
            }
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Preview_dispatches_exactly_three_short_lived_attempts_and_locks_out()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-cap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-cap-{Guid.NewGuid():N}";
        string externalReady = Path.Combine(directory, "external.ready");
        Process? preview = null, external = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--exit-after-ms", "100"], maximumAttempts: 3);
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);

            await WaitForLockoutAsync(preview, budgets, history, profileId,
                3, TimeSpan.FromSeconds(75));
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.LaunchDispatched, 3, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(8));

            SharedRecoveryBudget final = budgets.Load(profileId);
            Assert.True(final.LockedOut);
            Assert.Equal(3, final.ReservedAutomaticAttempts);
            EventHistoryResult reserved = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchReserved));
            EventHistoryResult dispatched = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched));
            Assert.Equal(3, reserved.TotalMatches);
            Assert.Equal(3, dispatched.TotalMatches);

            int observationsBeforeRestart = (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved)))
                .TotalMatches;
            StopStartedProcess(preview);
            preview = null;
            Assert.True(budgets.Load(profileId).LockedOut);
            preview = StartPreview(relightExecutable, directory);
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.False(preview.HasExited);
            Assert.True(budgets.Load(profileId).LockedOut);
            Assert.Equal(3, budgets.Load(profileId).ReservedAutomaticAttempts);
            Assert.Equal(3, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);

            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--ready-file");
            start.ArgumentList.Add(externalReady);
            external = Process.Start(start) ??
                throw new InvalidOperationException("External test target did not start.");
            await WaitUntilAsync(() => File.Exists(externalReady), TimeSpan.FromSeconds(5));
            await WaitForTargetObservationsAsync(history, profileId,
                observationsBeforeRestart + 1, TimeSpan.FromSeconds(15));
            Assert.False(external.HasExited);
            Assert.True(budgets.Load(profileId).LockedOut);
            Assert.Equal(3, budgets.Load(profileId).ReservedAutomaticAttempts);
            Assert.Equal(3, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(external);
            StopLabeledTestTargets(targetExecutable, label);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Locked_out_external_start_rearms_only_after_stable_observation()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-rearm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-rearm-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "external.ready");
        Process? preview = null, external = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--exit-after-ms", "100"], maximumAttempts: 1);
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForLockoutAsync(preview, budgets, history, profileId,
                1, TimeSpan.FromSeconds(40));
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.LaunchDispatched, 1, TimeSpan.FromSeconds(10));
            EventHistoryResult previouslyObserved = await history.ReadAsync(
                new EventHistoryQuery(ProfileId: profileId,
                    Kind: OperationalEventKind.TargetObserved));

            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--ready-file");
            start.ArgumentList.Add(ready);
            external = Process.Start(start) ??
                throw new InvalidOperationException("External test target did not start.");
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(5));
            await WaitForTargetObservationsAsync(history, profileId,
                previouslyObserved.TotalMatches + 1,
                TimeSpan.FromSeconds(15));

            await Task.Delay(TimeSpan.FromSeconds(10));
            SharedRecoveryBudget? early = null;
            await WaitUntilAsync(() => (early = TryLoadBudget(budgets, profileId)) is not null,
                TimeSpan.FromSeconds(5));
            Assert.NotNull(early);
            Assert.True(early.LockedOut);
            Assert.Equal(1, early.ReservedAutomaticAttempts);
            Assert.False(external.HasExited);

            await WaitUntilAsync(() =>
            {
                if (preview.HasExited || external.HasExited)
                    throw new InvalidOperationException(
                        "Preview or external target exited before stable rearm.");
                SharedRecoveryBudget? budget = TryLoadBudget(budgets, profileId);
                return budget?.LockedOut == false &&
                    budget.ReservedAutomaticAttempts == 0;
            }, TimeSpan.FromSeconds(80));
            EventHistoryResult dispatched = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched));
            Assert.Equal(1, dispatched.TotalMatches);
            Assert.False(external.HasExited);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(external);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Locked_out_external_start_that_exits_early_keeps_its_budget()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-early-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-early-external-{Guid.NewGuid():N}";
        Process? preview = null, external = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--exit-after-ms", "100"], maximumAttempts: 1);
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForLockoutAsync(preview, budgets, history, profileId,
                1, TimeSpan.FromSeconds(40));
            int observedBefore = (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved)))
                .TotalMatches;
            int interruptedBefore = (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationInterrupted)))
                .TotalMatches;

            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--exit-after-ms");
            start.ArgumentList.Add("15000");
            external = Process.Start(start) ??
                throw new InvalidOperationException("External test target did not start.");
            await WaitForTargetObservationsAsync(history, profileId,
                observedBefore + 1, TimeSpan.FromSeconds(15));
            await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationInterrupted, interruptedBefore + 1,
                TimeSpan.FromSeconds(15));
            await Task.Delay(TimeSpan.FromSeconds(8));

            SharedRecoveryBudget budget = budgets.Load(profileId);
            Assert.True(budget.LockedOut);
            Assert.Equal(1, budget.ReservedAutomaticAttempts);
            EventHistoryResult dispatched = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched));
            Assert.Equal(1, dispatched.TotalMatches);
            Assert.False(preview.HasExited);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(external);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Third_automatic_attempt_can_complete_observation_and_reset_budget()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-third-stable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string attemptsFile = Path.Combine(directory, "attempts.bin");
        string ready = Path.Combine(directory, "target.ready");
        string label = $"wpf-third-stable-{Guid.NewGuid():N}";
        Process? preview = null;
        int? stablePid = null;
        long? stableStartedTicks = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--attempt-file", attemptsFile,
                    "--fail-first", "2", "--exit-after-ms", "100",
                    "--ready-file", ready], maximumAttempts: 3);
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.LaunchDispatched, 3, TimeSpan.FromSeconds(75));
            await WaitUntilAsync(() =>
            {
                SharedRecoveryBudget? budget = TryLoadBudget(budgets, profileId);
                if (budget?.ReservedAutomaticAttempts != 3 ||
                    !File.Exists(ready) || !File.Exists(attemptsFile)) return false;
                try
                {
                    byte[] count = File.ReadAllBytes(attemptsFile);
                    string[] identity = File.ReadAllText(ready).Split('|');
                    return count.Length == sizeof(int) &&
                        BitConverter.ToInt32(count) == 3 &&
                        IsSameTargetAlive(int.Parse(identity[0], CultureInfo.InvariantCulture),
                            DateTimeOffset.Parse(identity[1],
                                CultureInfo.InvariantCulture).UtcTicks, targetExecutable);
                }
                catch (Exception error) when (error is IOException or FormatException or
                    IndexOutOfRangeException) { return false; }
            }, TimeSpan.FromSeconds(15));
            string[] identity = (await File.ReadAllTextAsync(ready)).Split('|');
            stablePid = int.Parse(identity[0], CultureInfo.InvariantCulture);
            stableStartedTicks = DateTimeOffset.Parse(identity[1],
                CultureInfo.InvariantCulture).UtcTicks;
            Assert.True(IsSameTargetAlive(stablePid.Value, stableStartedTicks.Value,
                targetExecutable));

            await Task.Delay(TimeSpan.FromSeconds(10));
            SharedRecoveryBudget? observing = TryLoadBudget(budgets, profileId);
            Assert.NotNull(observing);
            Assert.Equal(3, observing.ReservedAutomaticAttempts);
            Assert.False(observing.LockedOut);
            await WaitUntilAsync(() =>
            {
                if (preview.HasExited || !IsSameTargetAlive(stablePid.Value,
                        stableStartedTicks.Value, targetExecutable))
                    throw new InvalidOperationException(
                        "The third attempt exited before completing observation.");
                SharedRecoveryBudget? budget = TryLoadBudget(budgets, profileId);
                return budget?.ReservedAutomaticAttempts == 0 &&
                    budget.LockedOut == false;
            }, TimeSpan.FromSeconds(80));

            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationCompleted, 1,
                TimeSpan.FromSeconds(10));

            EventHistoryResult dispatched = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched));
            EventHistoryResult stable = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationCompleted));
            EventHistoryResult lockouts = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LockoutEntered));
            Assert.Equal(3, dispatched.TotalMatches);
            Assert.Contains(stable.Events, entry =>
                entry.Origin == ObservationOrigin.AutomaticLaunch);
            Assert.Empty(lockouts.Events);
            Assert.Equal(3, BitConverter.ToInt32(File.ReadAllBytes(attemptsFile)));
        }
        finally
        {
            StopStartedProcess(preview);
            if (stablePid is { } pid && stableStartedTicks is { } started &&
                IsSameTargetAlive(pid, started, targetExecutable))
            {
                using Process target = Process.GetProcessById(pid);
                target.Kill(entireProcessTree: false);
                target.WaitForExit(5000);
            }
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Late_observation_exit_keeps_attempt_charged_without_recovery_reset()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-late-exit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string ready = Path.Combine(directory, "target.ready");
        string label = $"wpf-late-exit-{Guid.NewGuid():N}";
        Process? preview = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--exit-after-ms", "58000",
                    "--ready-file", ready], maximumAttempts: 1,
                normalPollInterval: TimeSpan.FromSeconds(60));
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationStarted, 1,
                TimeSpan.FromSeconds(25));
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(5));
            string[] identity = (await File.ReadAllTextAsync(ready)).Split('|');
            int targetPid = int.Parse(identity[0], CultureInfo.InvariantCulture);
            long targetStartedTicks = DateTimeOffset.Parse(identity[1],
                CultureInfo.InvariantCulture).UtcTicks;
            Assert.True(IsSameTargetAlive(targetPid, targetStartedTicks,
                targetExecutable));
            Assert.Equal(1, budgets.Load(profileId).ReservedAutomaticAttempts);

            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationInterrupted, 1,
                TimeSpan.FromSeconds(75));
            Assert.False(IsSameTargetAlive(targetPid, targetStartedTicks,
                targetExecutable));
            Assert.Equal(1, budgets.Load(profileId).ReservedAutomaticAttempts);
            await WaitForLockoutAsync(preview, budgets, history, profileId,
                1, TimeSpan.FromSeconds(75));
            EventHistoryResult started = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationStarted));
            EventHistoryResult interrupted = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationInterrupted));
            OperationalEvent start = Assert.Single(started.Events);
            OperationalEvent end = Assert.Single(interrupted.Events,
                entry => entry.NewState == RecoveryState.AwaitingIntervention);
            Assert.Equal(ObservationOrigin.AutomaticLaunch, start.Origin);
            Assert.Equal(ObservationOrigin.AutomaticLaunch, end.Origin);
            if (end.FailureCategory != OperationalFailureCategory.EarlyExit)
                Assert.True((await history.ReadAsync(new EventHistoryQuery(
                    ProfileId: profileId, Kind: OperationalEventKind.DetectionUnavailable)))
                    .TotalMatches > 0);
            Assert.True(end.OccurredUtc - start.OccurredUtc >=
                TimeSpan.FromSeconds(50));
            Assert.False(IsSameTargetAlive(targetPid, targetStartedTicks,
                targetExecutable));
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationCompleted)))
                .TotalMatches);
            Assert.Equal(1, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);
            SharedRecoveryBudget budget = budgets.Load(profileId);
            Assert.True(budget.LockedOut);
            Assert.Equal(1, budget.ReservedAutomaticAttempts);
            Assert.False(preview.HasExited);
        }
        finally
        {
            StopStartedProcess(preview);
            StopLabeledTestTargets(targetExecutable, label);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task External_start_during_retry_countdown_averts_automatic_launch()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-countdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-countdown-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "external.ready");
        Process? preview = null, external = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--ready-file", ready], maximumAttempts: 3,
                retryDelay: TimeSpan.FromSeconds(30));
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForStateAsync(history, profileId, RecoveryState.RetryWaiting,
                TimeSpan.FromSeconds(10));
            Assert.False(preview.HasExited);
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);

            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--ready-file");
            start.ArgumentList.Add(ready);
            external = Process.Start(start) ??
                throw new InvalidOperationException("External test target did not start.");
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(5));
            int externalPid = external.Id;
            long externalStartedTicks = external.StartTime.ToUniversalTime().Ticks;

            await WaitForTargetObservationsAsync(history, profileId, 1,
                TimeSpan.FromSeconds(40));
            EventHistoryResult observed = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved));
            Assert.Contains(observed.Events, entry =>
            {
                string[] fields = entry.ProcessIdentity?.Split('|') ?? [];
                return fields.Length == 4 &&
                    fields[2] == externalPid.ToString(CultureInfo.InvariantCulture) &&
                    fields[3] == externalStartedTicks.ToString(CultureInfo.InvariantCulture);
            });
            Assert.True(IsSameTargetAlive(externalPid, externalStartedTicks,
                targetExecutable));
            Assert.False(preview.HasExited);
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);
            EventHistoryResult reserved = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchReserved));
            EventHistoryResult dispatched = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched));
            Assert.Equal(0, reserved.TotalMatches);
            Assert.Equal(0, dispatched.TotalMatches);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(external);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Initially_absent_wait_for_first_start_adopts_external_target_without_launch()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-first-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-first-start-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "external.ready");
        Process? preview = null, external = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--ready-file", ready], maximumAttempts: 3,
                initialAutomaticStart: false,
                normalPollInterval: TimeSpan.FromSeconds(60));
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForEventCountAsync(history, null,
                OperationalEventKind.Startup, 1, TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(8));
            Assert.False(preview.HasExited);
            Assert.False(File.Exists(ready));
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);

            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--ready-file");
            start.ArgumentList.Add(ready);
            external = Process.Start(start) ??
                throw new InvalidOperationException("External test target did not start.");
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(5));
            int externalPid = external.Id;
            long externalStartedTicks = external.StartTime.ToUniversalTime().Ticks;

            await WaitForTargetObservationsAsync(history, profileId, 1,
                TimeSpan.FromSeconds(75));
            EventHistoryResult observed = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved));
            Assert.Contains(observed.Events, entry =>
            {
                string[] fields = entry.ProcessIdentity?.Split('|') ?? [];
                return fields.Length == 4 &&
                    fields[2] == externalPid.ToString(CultureInfo.InvariantCulture) &&
                    fields[3] == externalStartedTicks.ToString(CultureInfo.InvariantCulture);
            });
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationCompleted, 1,
                TimeSpan.FromSeconds(75));
            EventHistoryResult completed = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationCompleted));
            Assert.Contains(completed.Events, entry =>
                entry.Origin == ObservationOrigin.InitialAdoption);
            Assert.True(IsSameTargetAlive(externalPid, externalStartedTicks,
                targetExecutable));
            Assert.False(preview.HasExited);
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchReserved)))
                .TotalMatches);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(external);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Already_running_target_is_adopted_without_duplicate_launch()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-adoption-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-adoption-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "existing.ready");
        Process? preview = null, existing = null;
        try
        {
            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--ready-file");
            start.ArgumentList.Add(ready);
            existing = Process.Start(start) ??
                throw new InvalidOperationException("Existing test target did not start.");
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(5));
            int existingPid = existing.Id;
            long existingStartedTicks = existing.StartTime.ToUniversalTime().Ticks;

            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--ready-file", ready], maximumAttempts: 3);
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForTargetObservationsAsync(history, profileId, 1,
                TimeSpan.FromSeconds(15));
            EventHistoryResult observed = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved));
            Assert.Contains(observed.Events, entry =>
            {
                string[] fields = entry.ProcessIdentity?.Split('|') ?? [];
                return fields.Length == 4 &&
                    fields[2] == existingPid.ToString(CultureInfo.InvariantCulture) &&
                    fields[3] == existingStartedTicks.ToString(CultureInfo.InvariantCulture);
            });
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationCompleted, 1,
                TimeSpan.FromSeconds(75));
            EventHistoryResult completed = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.ObservationCompleted));
            Assert.Contains(completed.Events, entry =>
                entry.NewState == RecoveryState.Healthy &&
                entry.Origin == ObservationOrigin.InitialAdoption);
            Assert.True(IsSameTargetAlive(existingPid, existingStartedTicks,
                targetExecutable));
            Assert.False(preview.HasExited);
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchReserved)))
                .TotalMatches);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(existing);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Stable_target_exit_recovers_once_with_complete_event_chain()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-stable-exit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-stable-exit-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "target.ready");
        string attemptsFile = Path.Combine(directory, "attempts.bin");
        Process? preview = null, existing = null;
        int? recoveredPid = null;
        long? recoveredStartedTicks = null;
        try
        {
            var start = new ProcessStartInfo(targetExecutable) { UseShellExecute = false };
            start.ArgumentList.Add("--label");
            start.ArgumentList.Add(label);
            start.ArgumentList.Add("--ready-file");
            start.ArgumentList.Add(ready);
            existing = Process.Start(start) ??
                throw new InvalidOperationException("Initial test target did not start.");
            await WaitUntilAsync(() => File.Exists(ready), TimeSpan.FromSeconds(5));
            int initialPid = existing.Id;

            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--ready-file", ready,
                    "--attempt-file", attemptsFile], maximumAttempts: 3,
                normalPollInterval: TimeSpan.FromSeconds(60));
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationCompleted, 1,
                TimeSpan.FromSeconds(75));
            Assert.False(File.Exists(attemptsFile));
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);

            StopStartedProcess(existing);
            existing = null;
            File.Delete(ready);
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.TargetDisappeared, 1,
                TimeSpan.FromSeconds(20));
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.LaunchDispatched, 1,
                TimeSpan.FromSeconds(20));
            await WaitUntilAsync(() =>
            {
                if (!File.Exists(ready) || !File.Exists(attemptsFile)) return false;
                try
                {
                    string[] fields = File.ReadAllText(ready).Split('|');
                    return int.Parse(fields[0], CultureInfo.InvariantCulture) != initialPid &&
                        BitConverter.ToInt32(File.ReadAllBytes(attemptsFile)) == 1;
                }
                catch (Exception error) when (error is IOException or FormatException or
                    IndexOutOfRangeException or ArgumentException) { return false; }
            }, TimeSpan.FromSeconds(15));
            string[] identity = (await File.ReadAllTextAsync(ready)).Split('|');
            recoveredPid = int.Parse(identity[0], CultureInfo.InvariantCulture);
            recoveredStartedTicks = DateTimeOffset.Parse(identity[1],
                CultureInfo.InvariantCulture).UtcTicks;
            Assert.True(IsSameTargetAlive(recoveredPid.Value, recoveredStartedTicks.Value,
                targetExecutable));
            Assert.Equal(1, budgets.Load(profileId).ReservedAutomaticAttempts);

            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.ObservationCompleted, 2,
                TimeSpan.FromSeconds(75));
            Assert.True(IsSameTargetAlive(recoveredPid.Value, recoveredStartedTicks.Value,
                targetExecutable));
            Assert.False(preview.HasExited);
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);

            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Limit: 100));
            OperationalEvent disappearance = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.TargetDisappeared);
            OperationalEvent retry = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.StateChanged &&
                entry.NewState == RecoveryState.RetryWaiting);
            OperationalEvent reservation = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.LaunchReserved);
            OperationalEvent dispatch = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.LaunchDispatched);
            OperationalEvent observed = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.TargetObserved &&
                entry.ProcessIdentity?.Split('|').ElementAtOrDefault(2) ==
                    recoveredPid.Value.ToString(CultureInfo.InvariantCulture));
            OperationalEvent observing = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.ObservationStarted &&
                entry.Origin == ObservationOrigin.AutomaticLaunch);
            OperationalEvent recovered = Assert.Single(events.Events, entry =>
                entry.Kind == OperationalEventKind.ObservationCompleted &&
                entry.Origin == ObservationOrigin.AutomaticLaunch);
            Assert.Equal(reservation.OperationId, dispatch.OperationId);
            Assert.Equal(disappearance.EpisodeId, reservation.EpisodeId);
            Assert.True(disappearance.OccurredUtc <= reservation.OccurredUtc);
            Assert.True(dispatch.OccurredUtc >= retry.OccurredUtc +
                TimeSpan.FromSeconds(5));
            Assert.True(dispatch.OccurredUtc <= observed.OccurredUtc);
            Assert.True(observed.OccurredUtc <= observing.OccurredUtc);
            Assert.True(observing.OccurredUtc <= recovered.OccurredUtc);
            Assert.Equal(1, BitConverter.ToInt32(File.ReadAllBytes(attemptsFile)));
        }
        finally
        {
            StopStartedProcess(preview);
            StopStartedProcess(existing);
            StopLabeledTestTargets(targetExecutable, label);
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Zero_attempt_limit_detects_absence_without_automatic_dispatch()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"relight-wpf-zero-attempts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string targetExecutable = FindBuiltExecutable("Relight.TestTarget",
            "Relight.TestTarget.exe");
        string relightExecutable = FindBuiltExecutable("Relight.App", "Relight.exe");
        string label = $"wpf-zero-{Guid.NewGuid():N}";
        string ready = Path.Combine(directory, "target.ready");
        Process? preview = null;
        try
        {
            Guid profileId = await ConfigureProfileAsync(directory, targetExecutable,
                ["--label", label, "--ready-file", ready], maximumAttempts: 0);
            var budgets = new SharedRecoveryBudgetStore(directory);
            var history = new OperationalEventHistoryReader(directory);
            preview = StartPreview(relightExecutable, directory);
            await WaitForEventCountAsync(history, profileId,
                OperationalEventKind.LockoutEntered, 1, TimeSpan.FromSeconds(20));
            await Task.Delay(TimeSpan.FromSeconds(7));
            Assert.False(preview.HasExited);
            Assert.False(File.Exists(ready));
            Assert.Equal(0, budgets.Load(profileId).ReservedAutomaticAttempts);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchReserved)))
                .TotalMatches);
            Assert.Equal(0, (await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.LaunchDispatched)))
                .TotalMatches);
        }
        finally
        {
            StopStartedProcess(preview);
            StopLabeledTestTargets(targetExecutable, label);
            DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task<Guid> ConfigureProfileAsync(string directory,
        string targetExecutable, IReadOnlyList<string> arguments, int maximumAttempts,
        TimeSpan? retryDelay = null, bool initialAutomaticStart = true,
        TimeSpan? normalPollInterval = null)
    {
        await using var setup = await RecoveryApplicationHost
            .OpenSharedSessionPreviewAsync(directory);
        Guid profileId = await setup.RegisterExecutableAsync("Disposable WPF target",
            targetExecutable, arguments, null);
        RecoveryPolicy policy = setup.GetProfileForEdit(profileId).Policy with
        {
            StartAutomaticallyWhenInitiallyAbsent = initialAutomaticStart,
            NormalPollInterval = normalPollInterval ?? RecoveryPolicy.Default.NormalPollInterval,
            MaximumAutomaticAttempts = maximumAttempts,
            RetryDelay = retryDelay ?? TimeSpan.FromSeconds(5),
            AppearanceTimeout = TimeSpan.FromSeconds(5),
            AbsenceConfirmationDelay = TimeSpan.FromSeconds(1),
            LockoutDiscoveryInterval = TimeSpan.FromSeconds(5),
            ObservationPollInterval = TimeSpan.FromSeconds(1),
            ObservationPeriod = TimeSpan.FromSeconds(60)
        };
        await setup.UpdateProfileBasicsAsync(profileId, "Disposable WPF target", policy);
        return profileId;
    }

    private static SharedRecoveryBudget? TryLoadBudget(
        SharedRecoveryBudgetStore budgets, Guid profileId)
    {
        try { return budgets.Load(profileId); }
        catch (RecoveryStateUnavailableException error) when (error.InnerException is IOException)
        {
            // The WPF host may briefly hold this profile's cross-process lock.
            return null;
        }
    }

    private static Process StartPreview(string executable, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--shared-session-preview");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--tray");
        return Process.Start(start) ??
            throw new InvalidOperationException("WPF preview did not start.");
    }

    private static string FindBuiltExecutable(string project, string file)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Relight.slnx")))
            root = Directory.GetParent(root)?.FullName;
        if (root is null) throw new DirectoryNotFoundException("Solution root not found.");
        string configuration = AppContext.BaseDirectory.Contains("\\Debug\\",
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        string path = Path.Combine(root, project == "Relight.App" ? "src" : "tests",
            project, "bin", configuration, "net10.0-windows", file);
        return File.Exists(path) ? path : throw new FileNotFoundException(file, path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellation.Token);
        }
    }

    private static async Task WaitForLockoutAsync(Process preview,
        SharedRecoveryBudgetStore budgets, OperationalEventHistoryReader history,
        Guid profileId, int attempts, TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(() =>
            {
                if (preview.HasExited)
                    throw new InvalidOperationException(
                        $"WPF preview exited before lockout: {preview.ExitCode}.");
                SharedRecoveryBudget? budget = TryLoadBudget(budgets, profileId);
                return budget?.LockedOut == true &&
                    budget.ReservedAutomaticAttempts == attempts;
            }, timeout);
        }
        catch (OperationCanceledException error)
        {
            SharedRecoveryBudget? budget = TryLoadBudget(budgets, profileId);
            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Limit: 20));
            throw new TimeoutException(
                $"WPF preview lockout did not reach {attempts} attempts in {timeout}. " +
                $"Preview exited: {preview.HasExited}; budget revision: {budget?.Revision}; " +
                $"reserved: {budget?.ReservedAutomaticAttempts}; locked out: {budget?.LockedOut}; " +
                $"recent events: {string.Join(", ", events.Events.Select(item => item.Kind))}.",
                error);
        }
    }

    private static async Task<int> WaitForTargetObservationsAsync(
        OperationalEventHistoryReader history, Guid profileId, int minimum,
        TimeSpan timeout)
        => await WaitForEventCountAsync(history, profileId,
            OperationalEventKind.TargetObserved, minimum, timeout);

    private static async Task WaitForStateAsync(OperationalEventHistoryReader history,
        Guid profileId, RecoveryState state, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.StateChanged,
                Limit: 20), cancellation.Token);
            if (events.Events.Any(entry => entry.NewState == state)) return;
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellation.Token);
        }
    }

    private static async Task<int> WaitForEventCountAsync(
        OperationalEventHistoryReader history, Guid? profileId,
        OperationalEventKind kind, int minimum, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: kind,
                Limit: 20), cancellation.Token);
            if (events.TotalMatches >= minimum) return events.TotalMatches;
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellation.Token);
        }
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup path escaped the temporary directory.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static bool IsSameTargetAlive(int pid, long startedTicks, string executable)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == startedTicks &&
                string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""),
                    Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void StopStartedProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
            }
        }
        finally { process.Dispose(); }
    }

    private static void StopLabeledTestTargets(string executable, string label)
    {
        // A dispatched target may not reach its ready-file callback before a
        // failed assertion. Query the unique test label so cleanup can still
        // stop only this test's disposable processes.
        using var search = new ManagementObjectSearcher(
            "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process " +
            "WHERE Name = 'Relight.TestTarget.exe'");
        foreach (ManagementObject candidate in search.Get())
        {
            using (candidate)
            {
                string? path = candidate["ExecutablePath"] as string;
                string? commandLine = candidate["CommandLine"] as string;
                if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase) ||
                    commandLine is null ||
                    !commandLine.Contains(label, StringComparison.Ordinal)) continue;
                int pid = Convert.ToInt32(candidate["ProcessId"], CultureInfo.InvariantCulture);
                try
                {
                    using Process target = Process.GetProcessById(pid);
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: false);
                        target.WaitForExit(5000);
                    }
                }
                catch (ArgumentException) { } // The candidate exited after enumeration.
            }
        }
    }
}
