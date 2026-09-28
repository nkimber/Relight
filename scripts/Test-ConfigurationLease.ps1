param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root ('artifacts\configuration-lease-probe-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $probe 'runner'
$data = Join-Path $probe 'data'
New-Item -ItemType Directory -Path $project, $data | Out-Null

$storageProject = Join-Path $root 'src\Relight.Storage\Relight.Storage.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$storageProject" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $project 'runner.csproj')
@'
using Relight.Storage;

string mode = args[0];
string data = args[1];
var store = new ConfigurationStore(data);
if (mode == "init")
{
    store.Initialize(RelightConfiguration.Empty);
    return;
}
if (mode == "hold")
{
    StoredConfiguration current = store.Load();
    using (store.AcquireTargetActionLease(current))
    {
        File.WriteAllText(args[2], "ready");
        while (!File.Exists(args[3]))
            await Task.Delay(50);
    }
    return;
}
if (mode == "read")
{
    store.Load();
    return;
}
if (mode == "save")
{
    try
    {
        StoredConfiguration current = store.Load();
        store.Save(current, RelightConfiguration.Empty);
        return;
    }
    catch (ConfigurationUnavailableException)
    {
        Environment.ExitCode = 3;
        return;
    }
}
throw new ArgumentException("Unknown probe mode.");
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs')

& dotnet build (Join-Path $project 'runner.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Configuration lease probe build failed.' }
$runner = Join-Path $project 'bin\Release\net10.0\runner.dll'
& dotnet $runner init $data
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize probe configuration.' }

$ready = Join-Path $probe 'ready'
$release = Join-Path $probe 'release'
$holder = $null
try
{
    $holder = Start-Process -FilePath 'dotnet' -ArgumentList @($runner, 'hold', $data, $ready, $release) -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline)
    {
        if ($holder.HasExited) { throw "Lease holder exited early with code $($holder.ExitCode)." }
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'Lease holder did not become ready.' }

    & dotnet $runner read $data
    if ($LASTEXITCODE -ne 0) { throw 'Concurrent configuration read did not succeed.' }
    & dotnet $runner save $data
    if ($LASTEXITCODE -ne 3) { throw "Concurrent configuration save returned $LASTEXITCODE; expected lease rejection." }
    New-Item -ItemType File -Path $release | Out-Null
    if (-not $holder.WaitForExit(15000)) { throw 'Lease holder did not exit.' }
    if ($holder.ExitCode -ne 0) { throw "Lease holder failed with code $($holder.ExitCode)." }
    & dotnet $runner save $data
    if ($LASTEXITCODE -ne 0) { throw 'Configuration save did not succeed after lease release.' }
    Write-Output 'PASS: a second process can read but cannot save during launch dispatch, then can save after release.'
}
finally
{
    if ($null -ne $holder -and -not $holder.HasExited)
    {
        New-Item -ItemType File -Path $release -Force | Out-Null
        if (-not $holder.WaitForExit(3000)) { Stop-Process -Id $holder.Id -Force }
    }
    if ($null -ne $holder) { $holder.Dispose() }
}
