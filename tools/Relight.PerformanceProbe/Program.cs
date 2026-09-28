using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Relight.Storage;
using Relight.Windows;

string? solution = Directory.GetCurrentDirectory();
while (solution is not null && !File.Exists(Path.Combine(solution, "Relight.slnx")))
    solution = Directory.GetParent(solution)?.FullName;
if (solution is null) throw new DirectoryNotFoundException("Run from the Relight checkout.");
int minutes = args.Length > 0 ? int.Parse(args[0]) : 15;
int count = args.Length > 1 ? int.Parse(args[1]) : 20;
if (minutes is < 1 or > 1440 || count is < 1 or > 50)
    throw new ArgumentOutOfRangeException(nameof(args), "Use 1–1440 minutes and 1–50 profiles.");
string app = Path.Combine(solution, "src", "Relight.App", "bin", "Release",
    "net10.0-windows", "Relight.exe");
string target = Path.Combine(solution, "tests", "Relight.TestTarget", "bin",
    "Release", "net10.0-windows", "Relight.TestTarget.exe");
if (!File.Exists(app) || !File.Exists(target))
    throw new FileNotFoundException("Build Relight.slnx in Release before measuring.");
string run = Path.Combine(solution, "artifacts", "performance",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
string data = Path.Combine(run, "Data");
string apps = Path.Combine(run, "Apps");
Directory.CreateDirectory(data);
Directory.CreateDirectory(apps);
var profileIds = new List<Guid>();
await using (var setup = await RecoveryApplicationHost.OpenSharedSessionPreviewAsync(data))
{
    for (int index = 0; index < count; index++)
    {
        string folder = Path.Combine(apps, $"RelightProbe{index:00}");
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "Relight.TestTarget.exe");
        File.Copy(target, copy);
        foreach (string suffix in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            File.Copy(Path.ChangeExtension(target, suffix),
                Path.Combine(folder, "Relight.TestTarget" + suffix));
        profileIds.Add(await setup.RegisterExecutableAsync($"Idle target {index + 1}", copy));
    }
}

var start = new ProcessStartInfo(app)
{
    UseShellExecute = false,
    WindowStyle = ProcessWindowStyle.Hidden
};
start.ArgumentList.Add("--shared-session-preview");
start.ArgumentList.Add(data);
start.ArgumentList.Add("--tray");
using Process process = Process.Start(start) ?? throw new InvalidOperationException(
    "Relight preview did not start.");
Console.WriteLine($"Probe data: {run}");
Console.WriteLine($"Started {count} isolated profiles in Relight PID {process.Id}.");
try
{
    await Task.Delay(TimeSpan.FromSeconds(30));
    process.Refresh();
    if (process.HasExited || process.MainWindowHandle != 0)
        throw new InvalidOperationException("Relight exited or its dashboard is open.");

    TimeSpan cpuStart = process.TotalProcessorTime;
    long startTimestamp = Stopwatch.GetTimestamp();
    long initialWorkingSet = process.WorkingSet64;
    long maxWorkingSet = initialWorkingSet;
    long minWorkingSet = maxWorkingSet;
    DateTimeOffset measuredAt = DateTimeOffset.UtcNow;
    while (Stopwatch.GetElapsedTime(startTimestamp) < TimeSpan.FromMinutes(minutes))
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle != 0)
            throw new InvalidOperationException("Relight exited or opened its dashboard during measurement.");
        maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64);
        minWorkingSet = Math.Min(minWorkingSet, process.WorkingSet64);
    }
    double elapsedSeconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
    double cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
    double percent = cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100;
    var budgets = new SharedRecoveryBudgetStore(data);
    int charged = profileIds.Count(id =>
        budgets.Load(id).ReservedAutomaticAttempts != 0);
    var result = new
    {
        MeasuredAtUtc = measuredAt,
        DurationSeconds = elapsedSeconds,
        Profiles = count,
        OperatingSystem = RuntimeInformation.OSDescription,
        LogicalProcessors = Environment.ProcessorCount,
        AppVersion = FileVersionInfo.GetVersionInfo(app).ProductVersion,
        DashboardClosed = true,
        CpuSeconds = cpuSeconds,
        TotalCpuPercent = percent,
        InitialWorkingSetBytes = initialWorkingSet,
        MinimumWorkingSetBytes = minWorkingSet,
        PeakWorkingSetBytes = maxWorkingSet,
        FinalWorkingSetBytes = process.WorkingSet64,
        ProfilesWithAutomaticAttempts = charged
    };
    string report = Path.Combine(run, "measurement.json");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result,
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"CPU: {percent:F3}% total across {Environment.ProcessorCount} logical processors.");
    Console.WriteLine($"Working set: {process.WorkingSet64 / 1048576.0:F1} MiB final, " +
        $"{maxWorkingSet / 1048576.0:F1} MiB peak.");
    Console.WriteLine($"Automatic attempt budgets charged: {charged}.");
    Console.WriteLine($"Report: {report}");
    if (charged != 0) throw new InvalidOperationException("An idle profile used an automatic attempt.");
}
finally
{
    process.Refresh();
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync();
    }
}
