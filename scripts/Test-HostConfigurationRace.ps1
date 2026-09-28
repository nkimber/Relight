param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root ('artifacts\host-configuration-race-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $probe 'runner'
$data = Join-Path $probe 'data'
New-Item -ItemType Directory -Path $project, $data | Out-Null

$windowsProject = Join-Path $root 'src\Relight.Windows\Relight.Windows.csproj'
$storageProject = Join-Path $root 'src\Relight.Storage\Relight.Storage.csproj'
$target = Join-Path $root 'tests\Relight.TestTarget\bin\Release\net10.0-windows\Relight.TestTarget.exe'
if (-not (Test-Path -LiteralPath $target)) {
    throw 'Build Relight.slnx in Release before running the host configuration race probe.'
}
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Relight.CrossProcessProbe</AssemblyName>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$windowsProject" />
    <ProjectReference Include="$storageProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $project 'runner.csproj')
@'
using Relight.Core;
using Relight.Engine;
using Relight.Storage;
using Relight.Windows;

string mode = args[0];
string data = args[1];
if (mode == "host")
{
    var launcher = new WaitingLauncher(args[3], args[4]);
    await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
        data, executableLauncherFactory: _ => launcher);
    Guid profile = await host.RegisterExecutableAsync("Disposable target", args[2]);
    await host.StartProfileNowAsync(profile);
    return;
}
if (mode == "hostauto")
{
    var clock = new FakeClock();
    var launcher = new WaitingLauncher(args[3], args[4]);
    Guid profile = Guid.NewGuid();
    string label = $"race-{profile:N}";
    var policy = RecoveryPolicy.Default with
    {
        StartAutomaticallyWhenInitiallyAbsent = true,
        RetryDelay = TimeSpan.FromSeconds(5)
    };
    var configuration = new RelightConfiguration(
        [new(profile, "Disposable target", true,
            new(TargetKind.Executable, args[2], ["--label", label],
                RequiredArgument: label), policy)], GlobalConfiguration.Default);
    new ConfigurationStore(data).Initialize(configuration);
    var budgets = new SharedRecoveryBudgetStore(data);
    budgets.Create(profile);
    var sessions = new RecoverySessionStateStore(data,
        WindowsLogonSessionIdentity.Current().StorageKey, budgets);
    sessions.Create(profile, new RecoveryMachine(policy).ExportCheckpoint());
    await using var host = await RecoveryApplicationHost.OpenSharedSessionAsync(
        data, clock, executableLauncherFactory: _ => launcher);
    foreach (int second in new[] { 0, 2, 7 })
    {
        clock.Elapsed = TimeSpan.FromSeconds(second);
        await Task.WhenAll(host.Pulse().Values);
    }
    if (budgets.Load(profile).ReservedAutomaticAttempts != 1)
        throw new InvalidOperationException("Automatic dispatch did not consume exactly one attempt.");
    return;
}
if (mode == "save")
{
    var store = new ConfigurationStore(data);
    try
    {
        StoredConfiguration current = store.Load();
        store.Save(current, current.Configuration);
        return;
    }
    catch (ConfigurationUnavailableException)
    {
        Environment.ExitCode = 3;
        return;
    }
}
if (mode == "checkbudget")
{
    Guid operation = Guid.ParseExact(File.ReadAllText(args[2]), "N");
    var store = new ConfigurationStore(data);
    Guid profile = store.Load().Configuration.Profiles.Single().Id;
    SharedRecoveryBudget budget = new SharedRecoveryBudgetStore(data).Load(profile);
    if (budget.ReservedAutomaticAttempts != 1 ||
        budget.PendingAutomaticOperationId != operation)
        throw new InvalidOperationException(
            "In-flight automatic operation is not durably reserved exactly once.");
    return;
}
throw new ArgumentException("Unknown probe mode.");

sealed class WaitingLauncher(string ready, string release) : IProcessLauncher
{
    public async Task LaunchAsync(Guid operationId, CancellationToken cancellationToken)
    {
        File.WriteAllText(ready, operationId.ToString("N"));
        while (!File.Exists(release))
            await Task.Delay(50, cancellationToken);
    }
}

sealed class FakeClock : IMonotonicClock
{
    public TimeSpan Elapsed { get; set; }
}

'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs')

& dotnet build (Join-Path $project 'runner.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Host configuration race probe build failed.' }
$runner = Join-Path $project 'bin\Release\net10.0-windows\Relight.CrossProcessProbe.dll'
$ready = Join-Path $probe 'ready'
$release = Join-Path $probe 'release'
$hostOutput = Join-Path $probe 'host-output.txt'
$hostError = Join-Path $probe 'host-error.txt'
$holder = $null
try {
    $holder = Start-Process -FilePath 'dotnet' -ArgumentList @($runner, 'host', $data, $target, $ready, $release) -PassThru -WindowStyle Hidden -RedirectStandardOutput $hostOutput -RedirectStandardError $hostError
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline) {
        if ($holder.HasExited) { throw "Host exited before launch with code $($holder.ExitCode): $(Get-Content -LiteralPath $hostError -Raw)" }
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'Host launch did not become ready.' }

    & dotnet $runner save $data
    if ($LASTEXITCODE -ne 3) { throw "Concurrent configuration save returned $LASTEXITCODE; expected lease rejection." }
    New-Item -ItemType File -Path $release | Out-Null
    if (-not $holder.WaitForExit(15000)) { throw 'Host did not complete launch.' }
    if ($holder.ExitCode -ne 0) { throw "Host failed with code $($holder.ExitCode): $(Get-Content -LiteralPath $hostError -Raw)" }
    & dotnet $runner save $data
    if ($LASTEXITCODE -ne 0) { throw 'Configuration save did not succeed after host dispatch.' }
    Write-Output 'PASS: host manual dispatch excludes a concurrent cross-process configuration save; save succeeds after release.'
}
finally {
    if ($null -ne $holder -and -not $holder.HasExited) {
        New-Item -ItemType File -Path $release -Force | Out-Null
        if (-not $holder.WaitForExit(3000)) { Stop-Process -Id $holder.Id -Force }
    }
    if ($null -ne $holder) { $holder.Dispose() }
}

$autoData = Join-Path $probe 'auto-data'
New-Item -ItemType Directory -Path $autoData | Out-Null
$autoReady = Join-Path $probe 'auto-ready'
$autoRelease = Join-Path $probe 'auto-release'
$autoOutput = Join-Path $probe 'auto-output.txt'
$autoError = Join-Path $probe 'auto-error.txt'
$autoHolder = $null
try {
    $autoHolder = Start-Process -FilePath 'dotnet' -ArgumentList @($runner, 'hostauto', $autoData, $target, $autoReady, $autoRelease) -PassThru -WindowStyle Hidden -RedirectStandardOutput $autoOutput -RedirectStandardError $autoError
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $autoReady) -and [DateTime]::UtcNow -lt $deadline) {
        if ($autoHolder.HasExited) { throw "Automatic host exited before launch with code $($autoHolder.ExitCode): $(Get-Content -LiteralPath $autoError -Raw)" }
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $autoReady)) { throw 'Automatic host launch did not become ready.' }
    & dotnet $runner checkbudget $autoData $autoReady
    if ($LASTEXITCODE -ne 0) { throw 'In-flight automatic operation did not match the durable budget.' }
    & dotnet $runner save $autoData
    if ($LASTEXITCODE -ne 3) { throw "Concurrent configuration save during automatic dispatch returned $LASTEXITCODE; expected lease rejection." }
    New-Item -ItemType File -Path $autoRelease | Out-Null
    if (-not $autoHolder.WaitForExit(15000)) { throw 'Automatic host did not complete launch.' }
    if ($autoHolder.ExitCode -ne 0) { throw "Automatic host failed with code $($autoHolder.ExitCode): $(Get-Content -LiteralPath $autoError -Raw)" }
    & dotnet $runner save $autoData
    if ($LASTEXITCODE -ne 0) { throw 'Configuration save did not succeed after automatic dispatch.' }
    Write-Output 'PASS: host automatic dispatch charges one attempt before launch and excludes a concurrent cross-process configuration save.'
}
finally {
    if ($null -ne $autoHolder -and -not $autoHolder.HasExited) {
        New-Item -ItemType File -Path $autoRelease -Force | Out-Null
        if (-not $autoHolder.WaitForExit(3000)) { Stop-Process -Id $autoHolder.Id -Force }
    }
    if ($null -ne $autoHolder) { $autoHolder.Dispose() }
}
