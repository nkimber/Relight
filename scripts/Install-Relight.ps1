param(
    [switch]$EnableStartup
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$data = Join-Path $env:LOCALAPPDATA 'Relight'
$versions = Join-Path $data 'Versions'
$version = Join-Path $versions ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $version -Force | Out-Null

dotnet publish (Join-Path $repository 'src/Relight.App/Relight.App.csproj') -c Release -r win-x64 --self-contained true -o $version
if ($LASTEXITCODE -ne 0) { throw 'Relight publish failed; the installed version was not changed.' }
$executable = Join-Path $version 'Relight.exe'
if (-not (Test-Path -LiteralPath $executable) -or
    -not (Test-Path -LiteralPath (Join-Path $version 'Relight.dll'))) {
    throw 'The published Relight binaries are incomplete.'
}

$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$previous = (Get-ItemProperty -Path $runPath -Name Relight -ErrorAction SilentlyContinue).Relight
if ($previous -and $previous -notmatch '^"[^"]+[\\/]Relight\.exe" --tray$') {
    throw 'A different Relight startup entry exists; the installed version was not changed.'
}
if ($EnableStartup -or $previous) {
    Set-ItemProperty -Path $runPath -Name Relight -Value ('"' + $executable + '" --tray')
}

$pointer = Join-Path $data 'installed-version.json'
$temporary = $pointer + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
@{ ExecutablePath = $executable } | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
[System.IO.File]::Move($temporary, $pointer, $true)

$current = @(Get-CimInstance Win32_Process -Filter "name='Relight.exe'" |
    Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId -and
        $_.CommandLine -notlike '*--shared-session-preview*' })
if ($current.Count -eq 0) {
    Start-Process -FilePath $executable -ArgumentList '--tray'
    Write-Output "Installed and started Relight: $executable"
} else {
    Write-Output "Installed Relight: $executable"
    Write-Output 'A running Relight with version monitoring will switch to it shortly. An older build must be exited and restarted once.'
}
