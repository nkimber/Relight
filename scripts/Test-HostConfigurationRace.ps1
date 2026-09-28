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
