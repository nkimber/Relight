param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root ('artifacts\migration-interruption-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $probe 'runner'
New-Item -ItemType Directory -Path $project | Out-Null
$storageProject = Join-Path $root 'src\Relight.Storage\Relight.Storage.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Relight.MigrationProbe</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$storageProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $project 'runner.csproj')
@'
using Relight.Core;
using Relight.Storage;

string mode = args[0];
string data = args[1];
Guid profile = Guid.Parse(args[2]);
const string SessionKey = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
var legacy = new RecoveryStateStore(data);
var budgets = new SharedRecoveryBudgetStore(data);
var session = new RecoverySessionStateStore(data, SessionKey, budgets);
string original = Path.Combine(data, "State", $"{profile:N}.json");
string archive = Path.Combine(data, "State", $"{profile:N}.legacy.json");
string expected = Path.Combine(data, "expected.bin");
if (mode == "init")
{
    legacy.Create(profile, new RecoveryCheckpoint(true, true, true, true,
        2, Guid.NewGuid(), RecoveryState.AwaitingIntervention, null));
    File.Copy(original, expected);
    return;
}
if (mode == "migrate")
{
    MigrationBoundary stop = Enum.Parse<MigrationBoundary>(args[3]);
    legacy.MigrateToSession(profile, budgets, session, boundary =>
    {
        if (boundary != stop) return;
        File.WriteAllText(args[4], "ready");
        Thread.Sleep(Timeout.Infinite);
    });
    throw new InvalidOperationException("Migration returned without an interruption.");
}
if (mode == "verify")
{
    MigrationBoundary stop = Enum.Parse<MigrationBoundary>(args[3]);
    bool imported = stop is MigrationBoundary.BudgetImported or MigrationBoundary.SessionCreated;
    if (legacy.GetOwnership(profile) != LegacyStateOwnership.MigrationPending)
        throw new InvalidOperationException("Interrupted transfer lost its pending marker.");
    try { legacy.Load(profile); throw new InvalidOperationException("Legacy access was reopened."); }
    catch (RecoveryStateUnavailableException) { }
    if (imported)
    {
        legacy.RepairPendingMigration(profile, budgets, session);
        if (legacy.GetOwnership(profile) != LegacyStateOwnership.SessionOwner)
            throw new InvalidOperationException("Evidence-matched repair did not finish transfer.");
        SharedRecoveryBudget budget = budgets.Load(profile);
        if (budget.ReservedAutomaticAttempts != 2 || !budget.LockedOut ||
            !session.Load(profile).Checkpoint.Paused)
            throw new InvalidOperationException("Repair changed the protected budget or pause state.");
    }
    else
    {
        try
        {
            legacy.RepairPendingMigration(profile, budgets, session);
            throw new InvalidOperationException("Repair recreated a missing budget.");
        }
        catch (RecoveryStateUnavailableException) { }
        if (legacy.GetOwnership(profile) != LegacyStateOwnership.MigrationPending ||
            budgets.HasBudgetEvidence(profile) || session.HasStateEvidence(profile))
            throw new InvalidOperationException("Untrusted transfer was activated.");
    }
    if (!File.Exists(archive) || !File.ReadAllBytes(archive).AsSpan()
            .SequenceEqual(File.ReadAllBytes(expected)))
        throw new InvalidOperationException("Legacy bytes were not preserved.");
    return;
}
throw new ArgumentException("Unknown probe mode.");
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs')

& dotnet build (Join-Path $project 'runner.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Migration interruption probe build failed.' }
$runner = Join-Path $project 'bin\Release\net10.0\Relight.MigrationProbe.dll'
foreach ($boundary in @('OwnershipMarked', 'LegacyArchived', 'TombstoneWritten', 'BudgetImported', 'SessionCreated')) {
    $data = Join-Path $probe $boundary
    New-Item -ItemType Directory -Path $data | Out-Null
    $profile = [guid]::NewGuid().ToString()
    & dotnet $runner init $data $profile
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize $boundary migration case." }
    $ready = Join-Path $data 'ready'
    $output = Join-Path $data 'output.txt'
    $errorFile = Join-Path $data 'error.txt'
    $migrator = $null
    try {
        $migrator = Start-Process -FilePath 'dotnet' -ArgumentList @($runner, 'migrate', $data, $profile, $boundary, $ready) -PassThru -WindowStyle Hidden -RedirectStandardOutput $output -RedirectStandardError $errorFile
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline) {
            if ($migrator.HasExited) { throw "Migrator exited before $boundary with code $($migrator.ExitCode): $(Get-Content -LiteralPath $errorFile -Raw)" }
            Start-Sleep -Milliseconds 50
        }
        if (-not (Test-Path -LiteralPath $ready)) { throw "Migrator did not reach $boundary." }
        Stop-Process -Id $migrator.Id -Force
        if (-not $migrator.WaitForExit(15000)) { throw "Migrator did not stop at $boundary." }
        & dotnet $runner verify $data $profile $boundary
        if ($LASTEXITCODE -ne 0) { throw "Interrupted $boundary case failed verification." }
        Write-Output "PASS: interrupted $boundary migration preserved evidence and the required fail-closed/repair result."
    }
    finally {
        if ($null -ne $migrator -and -not $migrator.HasExited) { Stop-Process -Id $migrator.Id -Force }
        if ($null -ne $migrator) { $migrator.Dispose() }
    }
}
