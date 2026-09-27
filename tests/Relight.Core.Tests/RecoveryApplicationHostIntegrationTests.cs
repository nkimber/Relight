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
}
