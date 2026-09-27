using System.Diagnostics;

namespace Relight.Core.Tests;

public sealed class TestTargetIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Main_target_exits_after_configured_delay()
    {
        if (!OperatingSystem.IsWindows()) return;
        string ready = Path.Combine(Path.GetTempPath(), $"relight-target-{Guid.NewGuid():N}.ready");
        using Process process = Start("--label", "timed", "--ready-file", ready,
            "--exit-after-ms", "400");
        try
        {
            await WaitForFile(ready);
            Assert.True(process.MainWindowHandle != IntPtr.Zero, "Real target should expose a main window.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            KillIfRunning(process);
            File.Delete(ready);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Launcher_hands_off_to_a_distinct_real_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        string ready = Path.Combine(Path.GetTempPath(), $"relight-handoff-{Guid.NewGuid():N}.ready");
        using Process launcher = Start("--label", "handoff", "--handoff",
            "--ready-file", ready, "--exit-after-ms", "2500");
        Process? target = null;
        try
        {
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, launcher.ExitCode);
            string record = await WaitForFile(ready);
            int pid = int.Parse(record.Split('|')[0]);
            Assert.NotEqual(launcher.Id, pid);
            target = Process.GetProcessById(pid);
            Assert.False(target.HasExited);
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
        }
        finally
        {
            if (target is not null) { KillIfRunning(target); target.Dispose(); }
            KillIfRunning(launcher);
            File.Delete(ready);
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Helper_outlives_main_without_being_the_logical_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        string ready = Path.Combine(Path.GetTempPath(), $"relight-helper-{Guid.NewGuid():N}.ready");
        using Process main = Start("--label", "helper", "--spawn-helper",
            "--ready-file", ready, "--exit-after-ms", "400", "--helper-ms", "3500");
        Process? helper = null;
        try
        {
            string mainRecord = await WaitForFile(ready);
            string helperRecord = await WaitForFile(ready + ".helper");
            int helperPid = int.Parse(helperRecord.Split('|')[0]);
            Assert.NotEqual(main.Id, helperPid);
            Assert.Equal(main.Id, int.Parse(mainRecord.Split('|')[0]));
            helper = Process.GetProcessById(helperPid);
            await main.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(helper.HasExited);
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
        }
        finally
        {
            if (helper is not null) { KillIfRunning(helper); helper.Dispose(); }
            KillIfRunning(main);
            File.Delete(ready);
            File.Delete(ready + ".helper");
        }
    }

    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public async Task Target_can_accept_or_refuse_a_graceful_close()
    {
        if (!OperatingSystem.IsWindows()) return;
        string accepts = Path.Combine(Path.GetTempPath(), $"relight-close-{Guid.NewGuid():N}.ready");
        string refuses = Path.Combine(Path.GetTempPath(), $"relight-block-{Guid.NewGuid():N}.ready");
        using Process willing = Start("--label", "willing", "--ready-file", accepts);
        using Process blocked = Start("--label", "blocked", "--ready-file", refuses, "--block-close");
        try
        {
            await WaitForFile(accepts);
            await WaitForFile(refuses);
            willing.Refresh();
            blocked.Refresh();
            Assert.True(willing.CloseMainWindow());
            await willing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(blocked.CloseMainWindow());
            await Task.Delay(300);
            Assert.False(blocked.HasExited);
        }
        finally
        {
            KillIfRunning(willing);
            KillIfRunning(blocked);
            File.Delete(accepts);
            File.Delete(refuses);
        }
    }

    private static Process Start(params string[] args)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Relight.slnx")))
            directory = Directory.GetParent(directory)?.FullName;
        if (directory is null) throw new DirectoryNotFoundException("Relight solution root not found.");

        string configuration = AppContext.BaseDirectory.Contains("\\Debug\\", StringComparison.OrdinalIgnoreCase)
            ? "Debug" : "Release";
        string executable = Path.Combine(directory, "tests", "Relight.TestTarget", "bin",
            configuration, "net10.0-windows", "Relight.TestTarget.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the test target first.", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Test target did not start.");
    }

    private static async Task<string> WaitForFile(string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try { return await File.ReadAllTextAsync(path, cts.Token); }
                catch (IOException) { /* The target may still be writing the file. */ }
            }
            await Task.Delay(50, cts.Token);
        }
        throw new TimeoutException($"Test target did not report ready: {path}");
    }

    private static void KillIfRunning(Process process)
    {
        if (process.HasExited) return;
        process.Kill();
        process.WaitForExit(5000);
    }
}
