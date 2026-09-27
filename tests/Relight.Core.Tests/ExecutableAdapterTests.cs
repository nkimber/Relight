using System.Diagnostics;
using Relight.Core;
using Relight.Engine;
using Relight.Storage;
using Relight.Windows;

namespace Relight.Core.Tests;

[Collection("Windows desktop process tests")]
public sealed class ExecutableAdapterTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Coordinator_recovers_real_test_target_and_leaves_it_alive_on_dispose()
    {
        string ready = Path.Combine(Path.GetTempPath(), $"relight-coordinated-{Guid.NewGuid():N}.ready");
        string data = Path.Combine(Path.GetTempPath(), $"relight-coordinated-{Guid.NewGuid():N}");
        string label = $"relight-{Guid.NewGuid():N}";
        int? pid = null;
        try
        {
            var target = new ExecutableTarget(TestExecutable(),
                ["--label", label, "--ready-file", ready, "--exit-after-ms", "8000"],
                RequiredArgument: label);
            var clock = new FakeClock();
            var policy = RecoveryPolicy.Default with
            {
                StartAutomaticallyWhenInitiallyAbsent = true,
                RetryDelay = TimeSpan.FromSeconds(5)
            };
            var store = new RecoveryStateStore(data);
            Guid profile = Guid.NewGuid();
            using (var coordinator = ProfileCoordinator.CreateNew(profile, policy, store,
                       new ExecutableDiscovery(target), new ExecutableLauncher(target), clock))
            {
                clock.Elapsed = TimeSpan.Zero;
                await coordinator.TickAsync();
                clock.Elapsed = TimeSpan.FromSeconds(2);
                await coordinator.TickAsync();
                clock.Elapsed = TimeSpan.FromSeconds(7);
                CoordinatorResult launched = await coordinator.TickAsync();
                Assert.True(launched.LaunchDispatched,
                    $"State: {launched.Snapshot.State}; transition: {launched.Transition.Reason}; " +
                    $"storage: {launched.Error ?? "healthy"}");
                pid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
                clock.Elapsed = TimeSpan.FromSeconds(8);
                CoordinatorResult observed = await coordinator.TickAsync();
                Assert.Equal(RecoveryState.Observing, observed.Snapshot.State);
                Assert.Equal(1, store.Load(profile).Checkpoint.ReservedAutomaticAttempts);
            }
            using Process process = Process.GetProcessById(pid.Value);
            Assert.False(process.HasExited);
        }
        finally
        {
            KillTestProcess(pid);
            File.Delete(ready);
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Launch_and_discovery_identify_the_real_process_not_its_executable_name_alone()
    {
        string ready = Path.Combine(Path.GetTempPath(), $"relight-adapter-{Guid.NewGuid():N}.ready");
        string label = $"relight-{Guid.NewGuid():N}";
        var target = new ExecutableTarget(TestExecutable(),
            ["--label", label, "--ready-file", ready, "--exit-after-ms", "6000"],
            RequiredArgument: label);
        var discovery = new ExecutableDiscovery(target);
        var launcher = new ExecutableLauncher(target);
        int? pid = null;
        try
        {
            Assert.Equal(DetectionKind.Absent, (await discovery.DetectAsync(CancellationToken.None)).Kind);
            await launcher.LaunchAsync(Guid.NewGuid(), CancellationToken.None);
            pid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            Detection detected = await discovery.DetectAsync(CancellationToken.None);
            Assert.Equal(DetectionKind.Present, detected.Kind);
            Assert.Contains($"|{pid}|", detected.Identity);
            using Process process = Process.GetProcessById(pid.Value);
            process.Kill();
            await process.WaitForExitAsync();
            Assert.Equal(DetectionKind.Absent, (await discovery.DetectAsync(CancellationToken.None)).Kind);
        }
        finally
        {
            KillTestProcess(pid);
            File.Delete(ready);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Helper_does_not_mask_main_process_absence_when_excluded_by_exact_argument()
    {
        string ready = Path.Combine(Path.GetTempPath(), $"relight-main-{Guid.NewGuid():N}.ready");
        string label = $"relight-{Guid.NewGuid():N}";
        var target = new ExecutableTarget(TestExecutable(),
            ["--label", label, "--spawn-helper", "--helper-ms", "6000",
             "--exit-after-ms", "500", "--ready-file", ready],
            RequiredArgument: label, ExcludedArgument: "--helper");
        var discovery = new ExecutableDiscovery(target);
        var launcher = new ExecutableLauncher(target);
        int? mainPid = null, helperPid = null;
        try
        {
            await launcher.LaunchAsync(Guid.NewGuid(), CancellationToken.None);
            mainPid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            helperPid = int.Parse((await WaitForFile(ready + ".helper")).Split('|')[0]);
            using Process main = Process.GetProcessById(mainPid.Value);
            await main.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using Process helper = Process.GetProcessById(helperPid.Value);
            Assert.False(helper.HasExited);
            Assert.Equal(DetectionKind.Absent, (await discovery.DetectAsync(CancellationToken.None)).Kind);
        }
        finally
        {
            KillTestProcess(mainPid);
            KillTestProcess(helperPid);
            File.Delete(ready);
            File.Delete(ready + ".helper");
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { return await File.ReadAllTextAsync(path, cts.Token); }
                catch (IOException) { }
            }
            await Task.Delay(50, cts.Token);
        }
    }

    private static void KillTestProcess(int? pid)
    {
        if (pid is null) return;
        try
        {
            using Process process = Process.GetProcessById(pid.Value);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException) { }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }
}
