param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [switch]$Isolated
)

# Integration smoke check; only terminates processes started by this script.
# UI, tray-menu and close-to-tray acceptance must also be verified interactively.
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$existing = Get-Process -Name Relight -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId }
if ($existing -and -not $Isolated) { throw 'Exit the existing Relight instance (including Debug builds), or use -Isolated for disposable preview data.' }
$launchArguments = @('--tray', '--shell-test')
if ($Isolated) {
    $isolatedDirectory = Join-Path ([IO.Path]::GetTempPath()) ('relight-shell-' + [guid]::NewGuid().ToString('N'))
    $launchArguments = @('--tray', '--shared-session-preview', ('"{0}"' -f $isolatedDirectory))
    Write-Output "Isolated shell evidence: $isolatedDirectory (empty-profile host; normal data untouched)."
}
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-Preview {
    $process = Start-Process -FilePath $executablePath -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
    $started.Add($process)
    return $process
}

try {
    $primary = Start-Preview
    if (-not $primary.WaitForInputIdle(10000)) { throw 'Primary did not initialize within 10 seconds.' }
    if ($primary.HasExited) { throw 'Primary exited during startup.' }
    $primary.Refresh()
    if ($primary.MainWindowHandle -ne 0) { throw '--tray unexpectedly displayed a dashboard.' }
    Write-Output 'PASS: --tray starts a resident shell with no dashboard.'

    $second = Start-Preview
    if (-not $second.WaitForExit(10000)) { throw 'Second instance did not yield to primary.' }
    if ($second.ExitCode -ne 0) { throw "Second instance returned $($second.ExitCode)." }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $primary.Refresh()
        if ($primary.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($primary.HasExited -or $primary.MainWindowHandle -eq 0) {
        throw 'Second launch did not open the existing dashboard.'
    }
    Write-Output 'PASS: second launch exits successfully and activates the primary dashboard.'

    # A crash must release singleton ownership so a subsequent launch can start normally.
    Stop-Process -Id $primary.Id -Force
    $primary.WaitForExit()
    $replacement = Start-Preview
    if (-not $replacement.WaitForInputIdle(10000) -or $replacement.HasExited) {
        throw 'Shell could not restart after process interruption.'
    }
    Write-Output 'PASS: shell restarts after its prior process is interrupted.'
} finally {
    foreach ($process in $started) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $process.Dispose()
    }
}
