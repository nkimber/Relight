using System.Diagnostics;
using Relight.Core;
using Relight.Engine;
using Relight.Windows;

namespace Relight.Core.Tests;

[Collection("Windows desktop process tests")]
public sealed class ExecutableStopperTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Graceful_close_stops_only_a_freshly_verified_test_instance()
    {
        string label = $"relight-stop-{Guid.NewGuid():N}";
        string ready = Path.Combine(Path.GetTempPath(), label + ".ready");
        var target = Target(label, ready);
        int? pid = null;
        try
        {
            await new ExecutableLauncher(target).LaunchAsync(Guid.NewGuid(),
                CancellationToken.None);
            pid = int.Parse((await WaitForFile(ready)).Split('|')[0]);
            Detection selected = await new ExecutableDiscovery(target)
                .DetectAsync(CancellationToken.None);
            Assert.Equal(DetectionKind.Present, selected.Kind);

            TargetStopResult result = await new ExecutableStopper(target)
                .TryGracefulCloseAsync(selected.Identity!, TimeSpan.FromSeconds(3));

            Assert.Equal(TargetStopOutcome.Stopped, result.Outcome);
            Assert.Equal(DetectionKind.Absent, (await new ExecutableDiscovery(target)
                .DetectAsync(CancellationToken.None)).Kind);
        }
        finally
        {
            KillTestProcess(pid);
            File.Delete(ready);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Blocked_close_needs_separate_force_choice_and_stale_identity_cannot_kill_replacement()
    {
        string label = $"relight-blocked-{Guid.NewGuid():N}";
        string firstReady = Path.Combine(Path.GetTempPath(), label + ".first.ready");
        string secondReady = Path.Combine(Path.GetTempPath(), label + ".second.ready");
        var firstTarget = Target(label, firstReady, blockClose: true);
        int? firstPid = null, secondPid = null;
        try
        {
            await new ExecutableLauncher(firstTarget).LaunchAsync(Guid.NewGuid(),
                CancellationToken.None);
            firstPid = int.Parse((await WaitForFile(firstReady)).Split('|')[0]);
            Detection selected = await new ExecutableDiscovery(firstTarget)
                .DetectAsync(CancellationToken.None);
            Assert.Equal(DetectionKind.Present, selected.Kind);
            var stopper = new ExecutableStopper(firstTarget);

            TargetStopResult graceful = await stopper.TryGracefulCloseAsync(
                selected.Identity!, TimeSpan.FromMilliseconds(300));
            Assert.Equal(TargetStopOutcome.NeedsForceChoice, graceful.Outcome);
            using (Process process = Process.GetProcessById(firstPid.Value))
                Assert.False(process.HasExited);

            TargetStopResult forced = await stopper.ForceCloseAsync(selected.Identity!);
            Assert.Equal(TargetStopOutcome.Stopped, forced.Outcome);

            var replacement = Target(label, secondReady);
            await new ExecutableLauncher(replacement).LaunchAsync(Guid.NewGuid(),
                CancellationToken.None);
            secondPid = int.Parse((await WaitForFile(secondReady)).Split('|')[0]);
            TargetStopResult stale = await stopper.ForceCloseAsync(selected.Identity!);
            Assert.Equal(TargetStopOutcome.IdentityChanged, stale.Outcome);
            using Process stillRunning = Process.GetProcessById(secondPid.Value);
            Assert.False(stillRunning.HasExited);
        }
        finally
        {
            KillTestProcess(firstPid);
            KillTestProcess(secondPid);
            File.Delete(firstReady);
            File.Delete(secondReady);
        }
    }

    private static ExecutableTarget Target(string label, string ready, bool blockClose = false)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Relight.slnx")))
            root = Directory.GetParent(root)?.FullName;
        if (root is null) throw new DirectoryNotFoundException("Solution root not found.");
        string configuration = AppContext.BaseDirectory.Contains("\\Debug\\",
            StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        string executable = Path.Combine(root, "tests", "Relight.TestTarget", "bin",
            configuration, "net10.0-windows", "Relight.TestTarget.exe");
        var arguments = new List<string>
        {
            "--label", label, "--ready-file", ready, "--exit-after-ms", "10000"
        };
        if (blockClose) arguments.Add("--block-close");
        return new(executable, arguments, RequiredArgument: label);
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
                catch (IOException) { }
            }
            await Task.Delay(50, timeout.Token);
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
}
