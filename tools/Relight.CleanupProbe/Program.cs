using System.Diagnostics;
using System.Text.Json;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

// Deliberately uses the real clock, capacity reader and native deletion adapter.
if (args.Length > 0 && args[0] == "worker")
{
    string root = Path.GetFullPath(args[1]);
    if (!File.Exists(Path.Combine(root, "probe-owned.txt"))) throw new InvalidOperationException("Missing probe ownership marker.");
    using var service = new AutomaticCleanupService(Path.Combine(root, "data"));
    using var gate = new SemaphoreSlim(1);
    while (!File.Exists(Path.Combine(root, "stop")))
    {
        var pulse = await service.PulseAsync(gate);
        Write(Path.Combine(root, "worker.json"), new { Utc = DateTimeOffset.UtcNow, Environment.ProcessId,
            Session = Process.GetCurrentProcess().SessionId, pulse.Status });
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
    return;
}

int seconds = args.Length == 0 ? 8 * 3600 : int.Parse(args[0]);
if (seconds < 10 || seconds > 24 * 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
string evidence = Path.GetFullPath(Path.Combine("artifacts", "cleanup-soak", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
string files = Path.Combine(evidence, "files"), data = Path.Combine(evidence, "data");
Directory.CreateDirectory(files);
File.WriteAllText(Path.Combine(evidence, "probe-owned.txt"), "Disposable Relight cleanup probe; no real rules.");
for (int i = 0; i < 12; i++)
{
    string file = Path.Combine(files, $"candidate-{i:00}.log");
    File.WriteAllText(file, new string('x', 4096));
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30).AddMinutes(i));
}
string[] sentinels = [Path.Combine(files, "keep.log"), Path.Combine(files, "fresh.log"),
    Path.Combine(files, "valuable.txt"), Path.Combine(files, "nested", "old.log")];
foreach (string sentinel in sentinels)
{
    Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
    File.WriteAllText(sentinel, "preserve-disposable-sentinel");
    if (!sentinel.EndsWith("fresh.log")) File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddDays(-30));
}
var store = new CleanupStore(data);
var rule = new CleanupRule(Guid.NewGuid(), "Disposable overnight fixture", files, "*.log", ["keep.log"],
    RetainNewest: 0, MaximumFiles: 1, MaximumBytes: 8192);
var saved = store.SaveRules(0, [rule]);
var reviewer = new ReviewedCleanupService(data);
var preview = await reviewer.PreviewAsync(rule);
rule = reviewer.RuleForAutomaticApproval(preview.Id);
// High trigger exercises real capacity without filling the disk. Only this fixture is approved.
store.ApproveAutomatic(saved.Revision, rule, 1, 1024L * 1_073_741_824);
var watch = Stopwatch.StartNew();
var startedUtc = DateTimeOffset.UtcNow;
var lastPulse = TimeSpan.Zero;
var owner = new AutomaticCleanupService(data);
using var workGate = new SemaphoreSlim(1);
Process? competitor = null;
bool transferred = false, sawContention = false, sawTakeover = false;
string status = "Running", problem = "";
Console.WriteLine(evidence);
try
{
    await owner.PulseAsync(workGate); // Establish first ownership before starting the competitor.
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("worker"); start.ArgumentList.Add(evidence);
    competitor = Process.Start(start) ?? throw new IOException("Could not start competitor.");
    while (watch.Elapsed.TotalSeconds < seconds)
    {
        if (watch.Elapsed - lastPulse > TimeSpan.FromMinutes(2))
            throw new IOException("Observation gap exceeds two minutes; uninterrupted soak invalidated.");
        lastPulse = watch.Elapsed;
        if (competitor.HasExited) throw new IOException($"Competitor exited: {competitor.ExitCode}");
        if (!transferred && watch.Elapsed >= (seconds < 3 * 3600 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(70)))
        { owner.Dispose(); transferred = true; }
        if (!transferred) await owner.PulseAsync(workGate);
        string workerPath = Path.Combine(evidence, "worker.json");
        if (File.Exists(workerPath))
        {
            using var worker = JsonDocument.Parse(File.ReadAllText(workerPath));
            sawContention |= worker.RootElement.GetProperty("Status").GetString()!.Contains("owned by another");
            sawTakeover |= transferred && worker.RootElement.GetProperty("Status").GetString()!.Contains("armed for");
            if (DateTimeOffset.UtcNow - worker.RootElement.GetProperty("Utc").GetDateTimeOffset() > TimeSpan.FromSeconds(60))
                throw new IOException("Competitor heartbeat is stale.");
        }
        Check();
        Write(Path.Combine(evidence, "progress.json"), new { Status = "Running", Utc = DateTimeOffset.UtcNow,
            Environment.ProcessId, CompetitorPid = competitor.Id, Session = Process.GetCurrentProcess().SessionId,
            PlannedSeconds = seconds, ElapsedSeconds = watch.Elapsed.TotalSeconds, transferred, sawContention, sawTakeover,
            Runs = store.LoadHistory().Length, WorkingSet = Environment.WorkingSet,
            CpuSeconds = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds });
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
    if (!sawContention) throw new IOException("No competing-process lease rejection observed.");
    if (!sawTakeover) throw new IOException("No competing-process ownership takeover observed.");
    if (seconds >= 3 * 3600 && (!transferred || store.LoadHistory().Length < 2))
        throw new IOException("Expected elapsed-time runs and ownership takeover were not observed.");
    if (seconds >= 8 * 3600 && store.LoadHistory().Length < 7)
        throw new IOException("Expected seven hourly runs across ownership takeover.");
    status = seconds >= 8 * 3600 ? "Completed" : "Short harness check completed";
}
catch (Exception error) { status = "Failed"; problem = error.ToString(); Environment.ExitCode = 1; }
finally
{
    owner.Dispose();
    File.WriteAllText(Path.Combine(evidence, "stop"), "stop owned competitor");
    if (competitor is not null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await competitor.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { competitor.Kill(); await competitor.WaitForExitAsync(); status = "Failed"; problem += " Competitor did not stop gracefully."; Environment.ExitCode = 1; }
        competitor.Dispose();
    }
    store.DisableAutomatic();
    try { Check(true); }
    catch (Exception error) { status = "Failed"; problem += error.ToString(); Environment.ExitCode = 1; }
    Write(Path.Combine(evidence, "report.json"), new { Status = status, Problem = problem,
        Utc = DateTimeOffset.UtcNow, ElapsedSeconds = watch.Elapsed.TotalSeconds,
        PlannedSeconds = seconds, Session = Process.GetCurrentProcess().SessionId,
        Machine = Environment.MachineName, Runtime = Environment.Version.ToString(),
        OS = Environment.OSVersion.ToString(), transferred, sawContention, sawTakeover,
        History = store.LoadHistory(), FixtureAutomaticEnabled = store.LoadRules().AutomaticEnabled,
        ActualTwoSignInTest = "Not run: only one Windows sign-in available." });
}

void Check(bool final = false)
{
    foreach (string sentinel in sentinels)
        if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "preserve-disposable-sentinel")
            throw new IOException("A protected disposable sentinel changed.");
    if (!final) return; // The live worker may be between native dispatch and its durable outcome.
    var history = store.LoadHistory().OrderBy(r => r.StartedUtc).ToArray();
    if (history.Length > 0 && history[0].StartedUtc - startedUtc < TimeSpan.FromHours(1))
        throw new IOException("First run occurred before the real one-hour interval.");
    foreach (var run in history)
        if (run.Deleted > 1 || run.Problem is not null || run.Origin != "Automatic" || run.FinishedUtc is null)
            throw new IOException("Unexpected automatic run outcome.");
    for (int i = 1; i < history.Length; i++)
        if (history[i].StartedUtc - history[i - 1].StartedUtc < TimeSpan.FromHours(1))
            throw new IOException("Automatic interval shortened.");
    if (12 - Directory.GetFiles(files, "candidate-*.log").Length != history.Sum(r => r.Deleted))
        throw new IOException("Native file count does not match durable audit history.");
}

static void Write(string path, object value)
{
    string staging = path + ".tmp";
    File.WriteAllText(staging, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    File.Move(staging, path, true);
}
