param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$probe = Join-Path $root ('artifacts\log-retention-lease-probe-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $probe 'runner'
$data = Join-Path $probe 'data'
$exports = Join-Path $probe 'exports'
$logs = Join-Path $data 'Logs'
New-Item -ItemType Directory -Path $project, $data, $exports, $logs | Out-Null

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

string mode = args[0], data = args[1];
if (mode == "append")
{
    using var journal = new OperationalEventJournal(data, GlobalConfiguration.Default);
    EventJournalStatus status = await journal.AppendAsync(new(
        DateTimeOffset.UtcNow, Guid.NewGuid(), EventSeverity.Information,
        OperationalEventKind.LaunchReserved));
    if (status.Degraded) throw new IOException(status.Diagnostic);
    return;
}
if (mode == "export")
{
    File.WriteAllText(args[3], "ready");
    await new OperationalEventHistoryReader(data).ExportAsync(new(), args[2],
        EventHistoryExportFormat.Csv);
    return;
}
throw new ArgumentException("Unknown probe mode.");
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs')

& dotnet build (Join-Path $project 'runner.csproj') -c Release -v:q
if ($LASTEXITCODE -ne 0) { throw 'Log-retention lease probe build failed.' }
$runner = Join-Path $project 'bin\Release\net10.0\runner.dll'
$old = Join-Path $logs ('events-20200101T0000000000000Z-' + [guid]::NewGuid().ToString('N') + '.jsonl')
Set-Content -LiteralPath $old -Value 'Old retained log'
[System.IO.File]::SetLastWriteTimeUtc($old, [DateTime]::UtcNow.AddDays(-40))
$gate = Join-Path $data '.relight-log-retention.lock'
$shared = $null
$exclusive = $null
$exporter = $null
try
{
    $shared = [System.IO.FileStream]::new($gate,
        [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::ReadWrite)
    & dotnet $runner append $data
    if ($LASTEXITCODE -ne 0) { throw 'Cross-process append failed while export lease was held.' }
    if (-not (Test-Path -LiteralPath $old)) { throw 'Retention deleted a log during export lease.' }
    $shared.Dispose()
    $shared = $null

    & dotnet $runner append $data
    if ($LASTEXITCODE -ne 0) { throw 'Cross-process append failed after export lease release.' }
    if (Test-Path -LiteralPath $old) { throw 'Retention did not resume after export lease release.' }

    $destination = Join-Path $exports 'history.csv'
    Set-Content -LiteralPath $destination -Value 'previous export'
    $ready = Join-Path $probe 'export-ready'
    $exclusive = [System.IO.FileStream]::new($gate,
        [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $exporter = Start-Process -FilePath 'dotnet' -ArgumentList @($runner, 'export', $data, $destination, $ready) -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline)
    {
        if ($exporter.HasExited) { throw "Exporter exited early with code $($exporter.ExitCode)." }
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'Exporter did not become ready.' }
    Start-Sleep -Milliseconds 200
    if ($exporter.HasExited) { throw 'Exporter did not wait for retention lease release.' }
    if ((Get-Content -LiteralPath $destination -Raw).Trim() -ne 'previous export')
    { throw 'Exporter replaced the destination before acquiring its lease.' }
    $exclusive.Dispose()
    $exclusive = $null
    if (-not $exporter.WaitForExit(15000)) { throw 'Exporter did not finish after lease release.' }
    if ($exporter.ExitCode -ne 0) { throw "Exporter failed with code $($exporter.ExitCode)." }
    if (-not (Select-String -LiteralPath $destination -Pattern 'LaunchReserved' -Quiet))
    { throw 'Export omitted a retained event.' }
    Write-Output 'PASS: retention defers during cross-process export and export waits for retention.'
}
finally
{
    if ($null -ne $shared) { $shared.Dispose() }
    if ($null -ne $exclusive) { $exclusive.Dispose() }
    if ($null -ne $exporter -and -not $exporter.HasExited)
    {
        if (-not $exporter.WaitForExit(3000)) { Stop-Process -Id $exporter.Id -Force }
    }
    if ($null -ne $exporter) { $exporter.Dispose() }
}
