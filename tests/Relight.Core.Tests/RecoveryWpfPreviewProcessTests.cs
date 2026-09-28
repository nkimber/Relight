using System.Diagnostics;
using System.Globalization;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

[Collection("Windows desktop process tests")]
public sealed class RecoveryWpfPreviewProcessTests
{
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

            first.Kill(entireProcessTree: false);
            await first.WaitForExitAsync();
            Assert.True(IsSameTargetAlive(targetPid.Value, targetStartedTicks.Value,
                targetExecutable));
            Assert.Equal(1, budgets.Load(profileId).ReservedAutomaticAttempts);

            restarted = StartPreview(relightExecutable, directory);
            await WaitForTargetObservationsAsync(history, profileId,
                observationsBeforeRestart + 1, TimeSpan.FromSeconds(15));
            Assert.False(restarted.HasExited);
            Assert.True(IsSameTargetAlive(targetPid.Value, targetStartedTicks.Value,
                targetExecutable));
            Assert.Equal(1, budgets.Load(profileId).ReservedAutomaticAttempts);
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
        Process? preview = null;
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
        }
        finally
        {
            StopStartedProcess(preview);
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
                initialAutomaticStart: false);
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

    private static async Task<Guid> ConfigureProfileAsync(string directory,
        string targetExecutable, IReadOnlyList<string> arguments, int maximumAttempts,
        TimeSpan? retryDelay = null, bool initialAutomaticStart = true)
    {
        await using var setup = await RecoveryApplicationHost
            .OpenSharedSessionPreviewAsync(directory);
        Guid profileId = await setup.RegisterExecutableAsync("Disposable WPF target",
            targetExecutable, arguments, null);
        RecoveryPolicy policy = setup.GetProfileForEdit(profileId).Policy with
        {
            StartAutomaticallyWhenInitiallyAbsent = initialAutomaticStart,
            NormalPollInterval = initialAutomaticStart
                ? RecoveryPolicy.Default.NormalPollInterval : TimeSpan.FromSeconds(60),
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
}
