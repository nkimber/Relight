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

    private static async Task<Guid> ConfigureProfileAsync(string directory,
        string targetExecutable, IReadOnlyList<string> arguments, int maximumAttempts)
    {
        await using var setup = await RecoveryApplicationHost
            .OpenSharedSessionPreviewAsync(directory);
        Guid profileId = await setup.RegisterExecutableAsync("Disposable WPF target",
            targetExecutable, arguments, null);
        RecoveryPolicy policy = setup.GetProfileForEdit(profileId).Policy with
        {
            StartAutomaticallyWhenInitiallyAbsent = true,
            MaximumAutomaticAttempts = maximumAttempts,
            RetryDelay = TimeSpan.FromSeconds(5),
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

    private static async Task<int> WaitForEventCountAsync(
        OperationalEventHistoryReader history, Guid profileId,
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
