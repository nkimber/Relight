using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Relight.Core;
using Relight.Storage;
using Relight.Windows;

int durationMinutes = args.Length > 0 ? int.Parse(args[0]) : 24 * 60;
if (durationMinutes is < 3 or > 24 * 60)
    throw new ArgumentOutOfRangeException(nameof(args), "Use 3–1440 minutes.");
string? repository = Directory.GetCurrentDirectory();
while (repository is not null && !File.Exists(Path.Combine(repository, "Relight.slnx")))
    repository = Directory.GetParent(repository)?.FullName;
if (repository is null) throw new DirectoryNotFoundException("Run from the Relight checkout.");
string relight = Path.Combine(repository, "src", "Relight.App", "bin", "Release",
    "net10.0-windows", "Relight.exe");
string targetSource = Path.Combine(repository, "tests", "Relight.TestTarget", "bin",
    "Release", "net10.0-windows", "Relight.TestTarget.exe");
if (!File.Exists(relight) || !File.Exists(targetSource))
    throw new FileNotFoundException("Build Relight.slnx in Release before the soak.");

string run = Path.Combine(repository, "artifacts", "soak",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
string data = Path.Combine(run, "Data");
string apps = Path.Combine(run, "Apps");
Directory.CreateDirectory(data);
Directory.CreateDirectory(apps);
string progressPath = Path.Combine(run, "progress.json");
Console.WriteLine($"Soak directory: {run}");

string CopyTarget(string name)
{
    string folder = Path.Combine(apps, name);
    Directory.CreateDirectory(folder);
    string copy = Path.Combine(folder, "Relight.TestTarget.exe");
    File.Copy(targetSource, copy);
    foreach (string suffix in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        File.Copy(Path.ChangeExtension(targetSource, suffix),
            Path.Combine(folder, "Relight.TestTarget" + suffix));
    return copy;
}

string handoffTarget = CopyTarget("Handoff");
string helperTarget = CopyTarget("Helper");
string handoffLabel = "soak-handoff-" + Guid.NewGuid().ToString("N")[..12];
string helperLabel = "soak-helper-" + Guid.NewGuid().ToString("N")[..12];
RecoveryPolicy policy = RecoveryPolicy.Default with
{
    StartAutomaticallyWhenInitiallyAbsent = true,
    AbsenceConfirmationDelay = TimeSpan.FromSeconds(1),
    RetryDelay = TimeSpan.FromSeconds(5),
    AppearanceTimeout = TimeSpan.FromSeconds(10),
    ObservationPeriod = TimeSpan.FromSeconds(60),
    ObservationPollInterval = TimeSpan.FromSeconds(1),
    NormalPollInterval = TimeSpan.FromSeconds(60)
};
var profiles = new Dictionary<string, Guid>();
await using (var setup = await RecoveryApplicationHost.OpenSharedSessionPreviewAsync(data))
{
    Guid first = await setup.RegisterExecutableAsync("Soak handoff", handoffTarget,
        ["--label", handoffLabel, "--hidden", "--handoff",
            "--exit-after-ms", "90000"], null, policy);
    ProfileConfiguration firstConfig = setup.GetProfileForEdit(first);
    await setup.UpdateProfileDefinitionAsync(first, firstConfig.Name,
        firstConfig.Target with { RequiredArgument = handoffLabel,
            ExcludedArgument = "--helper" }, policy, false, false);
    profiles.Add("handoff", first);

    Guid second = await setup.RegisterExecutableAsync("Soak helper", helperTarget,
        ["--label", helperLabel, "--hidden", "--spawn-helper",
            "--helper-ms", "160000", "--exit-after-ms", "90000"], null, policy);
    ProfileConfiguration secondConfig = setup.GetProfileForEdit(second);
    await setup.UpdateProfileDefinitionAsync(second, secondConfig.Name,
        secondConfig.Target with { RequiredArgument = helperLabel,
            ExcludedArgument = "--helper" }, policy, false, false);
    profiles.Add("helper", second);
}

var start = new ProcessStartInfo(relight)
{
    UseShellExecute = false,
    WindowStyle = ProcessWindowStyle.Hidden
};
start.ArgumentList.Add("--shared-session-preview");
start.ArgumentList.Add(data);
start.ArgumentList.Add("--tray");
using Process process = Process.Start(start) ??
    throw new InvalidOperationException("Relight soak preview did not start.");
var elapsed = Stopwatch.StartNew();
DateTimeOffset startedAt = DateTimeOffset.UtcNow;
long initialWorkingSet = 0, peakWorkingSet = 0, peakLogBytes = 0;
TimeSpan initialCpu = TimeSpan.Zero;

long LogBytes()
{
    string logs = Path.Combine(data, "Logs");
    if (!Directory.Exists(logs)) return 0;
    long total = 0;
    foreach (string path in Directory.EnumerateFiles(logs))
    {
        try { total += new FileInfo(path).Length; }
        catch (FileNotFoundException) { /* A journal file rotated during sampling. */ }
    }
    return total;
}

async Task<SharedRecoveryBudget> LoadBudgetAsync(Guid id)
{
    var store = new SharedRecoveryBudgetStore(data);
    for (int attempt = 0; ; attempt++)
    {
        try { return store.Load(id); }
        catch (RecoveryStateUnavailableException error) when
            (attempt < 50 && error.InnerException is IOException)
        {
            await Task.Delay(100);
        }
    }
}

async Task WriteProgressAsync(string status, string? error = null)
{
    var progress = new
    {
        Status = status,
        StartedAtUtc = startedAt,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
        PlannedMinutes = durationMinutes,
        RelightPid = process.Id,
        Profiles = profiles,
        InitialWorkingSetBytes = initialWorkingSet,
        PeakWorkingSetBytes = peakWorkingSet,
        CurrentWorkingSetBytes = process.HasExited ? (long?)null : process.WorkingSet64,
        CurrentLogBytes = LogBytes(),
        PeakLogBytes = peakLogBytes,
        Error = error
    };
    string temporary = progressPath + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(progress,
        new JsonSerializerOptions { WriteIndented = true }));
    File.Move(temporary, progressPath, overwrite: true);
}

try
{
    await Task.Delay(TimeSpan.FromSeconds(10));
    process.Refresh();
    if (process.HasExited || process.MainWindowHandle != 0)
        throw new InvalidOperationException("Soak preview exited or opened its dashboard.");
    initialWorkingSet = peakWorkingSet = process.WorkingSet64;
    initialCpu = process.TotalProcessorTime;
    await WriteProgressAsync("Running");
    while (elapsed.Elapsed < TimeSpan.FromMinutes(durationMinutes))
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle != 0)
            throw new InvalidOperationException("Soak preview exited or opened its dashboard.");
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        peakLogBytes = Math.Max(peakLogBytes, LogBytes());
        await WriteProgressAsync("Running");
    }

    var reader = new OperationalEventHistoryReader(data);
    var summaries = new Dictionary<string, EventHistorySummary>();
    foreach ((string name, Guid id) in profiles)
        summaries.Add(name, (await reader.ReadOverviewAsync(new EventHistoryQuery(
            ProfileId: id, Limit: 1))).Summary);
    var budgetSnapshots = new Dictionary<string, SharedRecoveryBudget>();
    foreach ((string name, Guid id) in profiles)
        budgetSnapshots.Add(name, await LoadBudgetAsync(id));
    double cpuSeconds = (process.TotalProcessorTime - initialCpu).TotalSeconds;
    var report = new
    {
        StartedAtUtc = startedAt,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        DurationSeconds = elapsed.Elapsed.TotalSeconds,
        PlannedMinutes = durationMinutes,
        OperatingSystem = RuntimeInformation.OSDescription,
        LogicalProcessors = Environment.ProcessorCount,
        AppVersion = FileVersionInfo.GetVersionInfo(relight).ProductVersion,
        Profiles = profiles,
        Summaries = summaries,
        Budgets = budgetSnapshots,
        CpuSeconds = cpuSeconds,
        TotalCpuPercent = cpuSeconds / elapsed.Elapsed.TotalSeconds /
            Environment.ProcessorCount * 100,
        InitialWorkingSetBytes = initialWorkingSet,
        PeakWorkingSetBytes = peakWorkingSet,
        FinalWorkingSetBytes = process.WorkingSet64,
        PeakLogBytes = peakLogBytes,
        FinalLogBytes = LogBytes()
    };
    await File.WriteAllTextAsync(Path.Combine(run, "report.json"),
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    if (summaries.Values.Any(item => item.AutomaticLaunchesDispatched == 0 ||
        item.StableAutomaticRecoveries == 0 || item.Lockouts != 0))
        throw new InvalidOperationException("Recovery or lockout outcomes did not meet the soak baseline.");
    if (budgetSnapshots.Values.Any(item => item.LockedOut))
        throw new InvalidOperationException("A profile remained locked out after the soak.");
    await WriteProgressAsync("Completed");
    Console.WriteLine($"Soak completed: {Path.Combine(run, "report.json")}");
}
catch (Exception error)
{
    await WriteProgressAsync("Failed", error.ToString());
    throw;
}
finally
{
    process.Refresh();
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync();
    }
    foreach (Process targetProcess in Process.GetProcessesByName("Relight.TestTarget"))
    {
        using (targetProcess)
        {
            try
            {
                string? path = targetProcess.MainModule?.FileName;
                if (!string.Equals(path, handoffTarget, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(path, helperTarget, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!targetProcess.HasExited)
                {
                    targetProcess.Kill(entireProcessTree: false);
                    await targetProcess.WaitForExitAsync();
                }
            }
            catch (Exception error) when (error is InvalidOperationException or
                System.ComponentModel.Win32Exception) { }
        }
    }
}
