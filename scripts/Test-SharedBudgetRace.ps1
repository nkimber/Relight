param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root ('artifacts\shared-budget-race-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $probe 'runner'
$data = Join-Path $probe 'data'
New-Item -ItemType Directory -Path $project, $data | Out-Null
$storageProject = Join-Path $root 'src\Relight.Storage\Relight.Storage.csproj'

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$storageProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $project 'runner.csproj')

@'
using Relight.Storage;

string mode = args[0];
string data = args[1];
Guid profile = Guid.Parse(args[2]);
Guid episode = Guid.Parse(args[3]);
var store = new SharedRecoveryBudgetStore(data);
if (mode == "seed")
{
    SharedRecoveryBudget budget = store.Create(profile);
    for (int attempt = 0; attempt < 2; attempt++)
    {
        Guid operation = Guid.NewGuid();
        budget = store.ReserveAutomatic(profile, budget.Revision, 3, episode, operation);
        budget = store.ResolveAutomatic(profile, budget.Revision, operation);
    }
    foreach (string sessionKey in new[]
    {
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
    })
        new RecoverySessionStateStore(data, sessionKey, store)
            .InitializeForNewSignIn(profile, enabled: true);
    return;
}
if (mode == "compete")
{
    // Each runner loads its own session checkpoint, then competes for one budget.
    string sessionKey = args[4];
    string ready = args[5];
    string release = args[6];
    var session = new RecoverySessionStateStore(data, sessionKey, store);
    session.Load(profile);
    SharedRecoveryBudget before = store.Load(profile);
    File.WriteAllText(ready, sessionKey);
    while (!File.Exists(release)) await Task.Delay(25);
    try
    {
        store.ReserveAutomatic(profile, before.Revision, 3, episode, Guid.NewGuid());
        return;
    }
    catch (StaleRecoveryRevisionException)
    {
        Environment.ExitCode = 3;
        return;
    }
}
if (mode == "verify")
{
    SharedRecoveryBudget budget = store.Load(profile);
    if (budget.ReservedAutomaticAttempts != 3 ||
        budget.PendingAutomaticOperationId is null || budget.EpisodeId != episode)
        throw new InvalidOperationException("Final reservation was not durable and exact.");
    budget = store.ResolveAutomatic(profile, budget.Revision,
        budget.PendingAutomaticOperationId.Value);
    try
    {
        store.ReserveAutomatic(profile, budget.Revision, 3, episode, Guid.NewGuid());
        throw new InvalidOperationException("A fourth attempt was incorrectly authorized.");
    }
    catch (InvalidOperationException error) when (error.Message ==
        "The shared recovery budget cannot authorize a launch.")
    {
        return;
    }
}
throw new ArgumentException("Unknown probe mode.");
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs')

& dotnet build (Join-Path $project 'runner.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Shared budget race probe build failed.' }
$runner = Join-Path $project 'bin\Release\net10.0-windows\runner.dll'
$profile = [guid]::NewGuid()
$episode = [guid]::NewGuid()
& dotnet $runner seed $data $profile $episode
if ($LASTEXITCODE -ne 0) { throw 'Could not seed two resolved attempts.' }

$release = Join-Path $probe 'release'
$children = @()
try {
    foreach ($session in @('AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', 'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB')) {
        $ready = Join-Path $probe "ready-$session"
        $stdout = Join-Path $probe "out-$session.txt"
        $stderr = Join-Path $probe "err-$session.txt"
        $child = Start-Process -FilePath 'dotnet' -ArgumentList @($runner, 'compete', $data, $profile, $episode, $session, $ready, $release) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $children += [pscustomobject]@{ Process = $child; Ready = $ready; Error = $stderr }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((@($children | Where-Object { -not (Test-Path -LiteralPath $_.Ready) })).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        foreach ($child in $children) {
            if ($child.Process.HasExited) { throw "Competitor exited before release: $(Get-Content -LiteralPath $child.Error -Raw)" }
        }
        Start-Sleep -Milliseconds 50
    }
    if (@($children | Where-Object { -not (Test-Path -LiteralPath $_.Ready) }).Count -gt 0) { throw 'Competitors did not reach the barrier.' }
    New-Item -ItemType File -Path $release | Out-Null
    foreach ($child in $children) {
        if (-not $child.Process.WaitForExit(15000)) { throw 'Competitor did not finish.' }
    }
    $codes = @($children | ForEach-Object { $_.Process.ExitCode } | Sort-Object)
    if ($codes.Count -ne 2 -or $codes[0] -ne 0 -or $codes[1] -ne 3) {
        throw "Expected one reservation and one stale-revision rejection; exit codes: $($codes -join ', ')."
    }
    & dotnet $runner verify $data $profile $episode
    if ($LASTEXITCODE -ne 0) { throw 'Final budget verification failed.' }
    Write-Output 'PASS: two independent processes using distinct synthetic session keys reserve one final attempt; shared budget caps at three.'
}
finally {
    New-Item -ItemType File -Path $release -Force | Out-Null
    foreach ($child in $children) {
        if (-not $child.Process.HasExited) {
            if (-not $child.Process.WaitForExit(3000)) { Stop-Process -Id $child.Process.Id -Force }
        }
        $child.Process.Dispose()
    }
}
