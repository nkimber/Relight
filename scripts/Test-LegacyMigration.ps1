param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root ('artifacts\legacy-migration-probe-' + [guid]::NewGuid().ToString('N'))
$snapshot = Join-Path $probe 'pre-marker'
$data = Join-Path $probe 'data'
New-Item -ItemType Directory -Path $probe, $data | Out-Null

$archiveZip = Join-Path $probe 'pre-marker.zip'
& git -C $root archive --format=zip -o $archiveZip bbd5879 src/Relight.Core src/Relight.Storage
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the pre-marker storage revision.' }
Expand-Archive -LiteralPath $archiveZip -DestinationPath $snapshot

$oldProject = Join-Path $probe 'old-runner'
$newProject = Join-Path $probe 'new-runner'
New-Item -ItemType Directory -Path $oldProject, $newProject | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\pre-marker\src\Relight.Storage\Relight.Storage.csproj" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $oldProject 'old-runner.csproj')
@'
using Relight.Core;
using Relight.Storage;

string root = Environment.GetEnvironmentVariable("RELIGHT_PROBE_ROOT")!;
Guid id = Guid.Parse(Environment.GetEnvironmentVariable("RELIGHT_PROBE_ID")!);
var store = new RecoveryStateStore(Path.Combine(root, "data"));
StoredRecoveryState cached = store.Create(id, new RecoveryCheckpoint(
    true, false, false, false, 0, null, RecoveryState.WaitingForFirstStart, null));
File.WriteAllText(Path.Combine(root, "ready"), "ready");
while (!File.Exists(Path.Combine(root, "go"))) await Task.Delay(25);
string save;
try { store.Save(id, cached.Revision, cached.Checkpoint); save = "SAVE_SUCCEEDED"; }
catch (Exception error) { save = error.GetType().Name; }
string create;
try { store.Create(id, cached.Checkpoint); create = "CREATE_SUCCEEDED"; }
catch (Exception error) { create = error.GetType().Name; }
File.WriteAllLines(Path.Combine(root, "result"), [save, create]);
'@ | Set-Content -LiteralPath (Join-Path $oldProject 'Program.cs')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\Relight.Storage\Relight.Storage.csproj" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $newProject 'new-runner.csproj')
@'
using Relight.Storage;

string root = Environment.GetEnvironmentVariable("RELIGHT_PROBE_ROOT")!;
Guid id = Guid.Parse(Environment.GetEnvironmentVariable("RELIGHT_PROBE_ID")!);
string data = Path.Combine(root, "data");
var budgets = new SharedRecoveryBudgetStore(data);
var session = new RecoverySessionStateStore(data,
    "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", budgets);
new RecoveryStateStore(data).MigrateToSession(id, budgets, session);
if (budgets.Load(id).ReservedAutomaticAttempts != 0)
    throw new InvalidOperationException("Migration changed the attempt budget.");
'@ | Set-Content -LiteralPath (Join-Path $newProject 'Program.cs')

& dotnet build (Join-Path $oldProject 'old-runner.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Old storage probe failed to build.' }
& dotnet build (Join-Path $newProject 'new-runner.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Current migration probe failed to build.' }

$env:RELIGHT_PROBE_ROOT = $probe
$id = [guid]::NewGuid()
$env:RELIGHT_PROBE_ID = $id.ToString()
$oldExe = Join-Path $oldProject 'bin\Release\net10.0\old-runner.exe'
$newExe = Join-Path $newProject 'bin\Release\net10.0\new-runner.exe'
$old = Start-Process -FilePath $oldExe -WindowStyle Hidden -PassThru
try {
    $deadline = (Get-Date).AddSeconds(10)
    $ready = Join-Path $probe 'ready'
    while (-not (Test-Path -LiteralPath $ready) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'Old storage probe did not become ready.' }
    $state = Join-Path $data ('State\' + $id.ToString('N') + '.json')
    $beforeHash = (Get-FileHash -LiteralPath $state -Algorithm SHA256).Hash

    & $newExe
    if ($LASTEXITCODE -ne 0) { throw 'Migration probe failed.' }
    New-Item -ItemType File -Path (Join-Path $probe 'go') | Out-Null
    Wait-Process -Id $old.Id -Timeout 10

    $outcome = @(Get-Content -LiteralPath (Join-Path $probe 'result'))
    $archived = Join-Path $data ('State\' + $id.ToString('N') + '.legacy.json')
    if ($outcome.Count -ne 2 -or
        $outcome[0] -ne 'RecoveryStateUnavailableException' -or
        $outcome[1] -ne 'InvalidOperationException' -or
        (Get-FileHash -LiteralPath $archived -Algorithm SHA256).Hash -ne $beforeHash -or
        (Get-Content -LiteralPath $state -Raw) -notmatch 'legacy access disabled') {
        throw "Legacy isolation failed: $($outcome -join ', ')"
    }
    Write-Output "PASS: pre-marker storage process rejected Save and Create after migration."
    Write-Output "PASS: original state bytes archived and legacy tombstone present."
    Write-Output "Evidence directory: $probe"
}
finally {
    if (-not $old.HasExited) { $old.Kill(); $old.WaitForExit() }
    Remove-Item Env:RELIGHT_PROBE_ROOT, Env:RELIGHT_PROBE_ID -ErrorAction SilentlyContinue
}
