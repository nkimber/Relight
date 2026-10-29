using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

// Only the child created here is terminated. No installed Relight instance or real scan is used.
if (args is ["--child", var directory])
{
    var ready = new TaskCompletionSource<Dispatcher>();
    var ui = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true };
    ui.SetApartmentState(ApartmentState.STA); ui.Start();
    var dispatcher = await ready.Task;
    using var blocked = new ManualResetEventSlim();
    _ = dispatcher.BeginInvoke(() => blocked.Wait(), DispatcherPriority.Send);
    await using var diagnostics = new ApplicationDiagnostics(directory,
        callback => dispatcher.BeginInvoke(callback, DispatcherPriority.Background),
        new("probe", "Release", Environment.Version.ToString(), Environment.OSVersion.ToString(), "X64", Environment.ProcessorCount,
            Process.GetCurrentProcess().SessionId), cadence: TimeSpan.FromSeconds(1), uiDelayThreshold: TimeSpan.FromSeconds(2));
    var scan = diagnostics.BeginScan(@"C:\disposable-secret", 100000)!;
    scan.Stage(DiagnosticScanStage.Traversing);
    scan.Operation(ScanIoOperation.ReadMetadata, @"C:\disposable-secret\blocked.vhdx", 1, 250, 200, 0, 0, 200);
    Console.WriteLine("READY");
    await Task.Delay(Timeout.Infinite);
    return;
}
if (args.Length != 1) throw new ArgumentException("Supply an isolated evidence directory.");
string root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("Use a fresh empty evidence directory.");
Directory.CreateDirectory(root);
var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
start.ArgumentList.Add("--child"); start.ArgumentList.Add(root);
using var child = Process.Start(start) ?? throw new InvalidOperationException("Probe did not start.");
var elapsed = Stopwatch.StartNew();
try
{
    if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) != "READY") throw new Exception("Child did not initialize.");
    string text = "";
    do
    {
        using var bytes = new MemoryStream();
        await new ApplicationDiagnosticReader(root).ExportAsync(bytes);
        text = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        if (text.Contains("DispatcherDelayed") && text.Contains("ScanHeartbeat") && text.Contains("\"IoPending\":true")) break;
        await Task.Delay(200);
    } while (elapsed.Elapsed < TimeSpan.FromSeconds(15));
    if (!text.Contains("DispatcherDelayed") || !text.Contains("\"IoPending\":true")) throw new Exception("Missing live watchdog evidence.");
    child.Kill(); await child.WaitForExitAsync();
    using var durable = new MemoryStream();
    var exported = await new ApplicationDiagnosticReader(root).ExportAsync(durable);
    string retained = System.Text.Encoding.UTF8.GetString(durable.ToArray());
    if (!retained.Contains("ProcessStarted") || !retained.Contains("DispatcherDelayed") || !retained.Contains("WorkingSet") ||
        retained.Contains("ProcessStopped") || retained.Contains("disposable-secret")) throw new Exception("Invalid durable/redacted termination evidence.");
    var report = new { Passed = true, Seconds = elapsed.Elapsed.TotalSeconds, Machine = Environment.MachineName,
        Session = Process.GetCurrentProcess().SessionId, ChildPid = child.Id, Records = exported.Records,
        Scope = "Synthetic dispatcher delay and pending metadata marker; child forced termination. No real C scan or original freeze reproduction." };
    await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(report));
}
finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
