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
            Guid profileId;
            await using (var setup = await RecoveryApplicationHost
                             .OpenSharedSessionPreviewAsync(directory))
            {
                profileId = await setup.RegisterExecutableAsync("Disposable WPF target",
                    targetExecutable,
                    ["--label", label, "--ready-file", ready], null);
                RecoveryPolicy policy = setup.GetProfileForEdit(profileId).Policy with
                {
                    StartAutomaticallyWhenInitiallyAbsent = true,
                    MaximumAutomaticAttempts = 1,
                    RetryDelay = TimeSpan.FromSeconds(5),
                    AppearanceTimeout = TimeSpan.FromSeconds(5),
                    AbsenceConfirmationDelay = TimeSpan.FromSeconds(1),
                    ObservationPollInterval = TimeSpan.FromSeconds(1),
                    ObservationPeriod = TimeSpan.FromSeconds(60)
                };
                await setup.UpdateProfileBasicsAsync(profileId,
                    "Disposable WPF target", policy);
            }

            first = StartPreview(relightExecutable, directory);
            var budgets = new SharedRecoveryBudgetStore(directory);
            await WaitUntilAsync(() =>
            {
                if (first.HasExited)
                    throw new InvalidOperationException(
                        $"WPF preview exited before recovery: {first.ExitCode}.");
                return File.Exists(ready) &&
                    budgets.Load(profileId).ReservedAutomaticAttempts == 1;
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
            string fullPath = Path.GetFullPath(directory);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup path escaped the temporary directory.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
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

    private static async Task<int> WaitForTargetObservationsAsync(
        OperationalEventHistoryReader history, Guid profileId, int minimum,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            EventHistoryResult events = await history.ReadAsync(new EventHistoryQuery(
                ProfileId: profileId, Kind: OperationalEventKind.TargetObserved,
                Limit: 20), cancellation.Token);
            if (events.TotalMatches >= minimum) return events.TotalMatches;
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellation.Token);
        }
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
