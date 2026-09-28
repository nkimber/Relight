param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe')
)

# Only launches and terminates the isolated preview processes started here.
# This checks WPF wiring, not two-sign-in isolation or real-target recovery.
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$existing = Get-Process -Name Relight -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $executablePath }
if ($existing) { throw 'Exit the existing Relight preview before running the smoke check.' }
$root = Split-Path $PSScriptRoot -Parent
$data = Join-Path $root ('artifacts\shared-session-preview-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
try
{
    $primary = Start-Process -FilePath $executablePath -ArgumentList @(
        '--shared-session-preview', $data, '--tray') -WindowStyle Hidden -PassThru
    $started.Add($primary)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $configuration = Join-Path $data 'configuration.json'
    while (-not (Test-Path -LiteralPath $configuration) -and [DateTime]::UtcNow -lt $deadline)
    {
        if ($primary.HasExited) { throw "Preview exited during startup with code $($primary.ExitCode)." }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $configuration))
    {
        throw 'Shared-session preview did not initialize its isolated configuration.'
    }
    $primary.Refresh()
    if ($primary.HasExited -or $primary.MainWindowHandle -ne 0)
    {
        throw 'Preview did not remain resident without an open dashboard.'
    }
    Write-Output 'PASS: WPF preview initialized isolated data and remained in the tray.'

    $second = Start-Process -FilePath $executablePath -ArgumentList @(
        '--shared-session-preview', $data, '--tray') -WindowStyle Hidden -PassThru
    $started.Add($second)
    if (-not $second.WaitForExit(10000) -or $second.ExitCode -ne 0)
    {
        throw 'Second preview instance did not yield to the resident shell.'
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do
    {
        $primary.Refresh()
        if ($primary.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($primary.HasExited -or $primary.MainWindowTitle -ne 'Relight — Shared-session preview')
    {
        throw 'Second launch did not open the preview dashboard.'
    }
    Write-Output 'PASS: second launch activated the clearly labeled preview dashboard.'
}
finally
{
    foreach ($process in $started)
    {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $process.Dispose()
    }
}
