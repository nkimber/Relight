param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [switch]$ExerciseScan
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
function Wait-For([scriptblock]$Condition, [string]$Description) {
    $until = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $until) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $Description."
}
function Read-Json([string]$Path) {
    try { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch [IO.IOException] { return $null }
    catch [UnauthorizedAccessException] { return $null }
}
function Find-Control($Parent, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Button($Parent, [string]$Name) {
    $control = Find-Control $Parent $Name
    if ($null -eq $control) { throw "Missing control: $Name" }
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('relight-storage-ui-' + [guid]::NewGuid().ToString('N'))
$primary = $null
try {
    $arguments = @('--shared-session-preview', $root, '--tray')
    $primary = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath (Join-Path $root 'configuration.json') } 'preview configuration' | Out-Null
    $opener = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $opener.WaitForExit(10000) -or $opener.ExitCode -ne 0) { throw 'Open dashboard failed.' }
    } finally { $opener.Dispose() }
    $dashboard = Wait-For {
        $primary.Refresh()
        if ($primary.MainWindowHandle -ne [IntPtr]::Zero) {
            $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($primary.MainWindowHandle)
            if ($candidate.Current.Name -eq 'Relight dashboard') { return $candidate }
        }
    } 'dashboard'
    Wait-For { Test-Path -LiteralPath (Join-Path $root 'configuration.json') } 'configuration' | Out-Null
    $settings = Wait-For {
        $candidate = Find-Control $dashboard 'Settings'
        if ($null -eq $candidate) { $candidate = Find-Control $dashboard '_Settings' }
        $candidate
    } 'settings navigation'
    $settings.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Button $dashboard 'Disk space monitoring…'
    $storage = Wait-For { Find-Control $dashboard 'Relight — Storage' } 'storage window'
    $readings = Wait-For {
        $all = $storage.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) {
            if ($item.Current.Name -like '*GiB*checked*') { return $item.Current.Name }
        }
    } 'native space reading'
    $interval = Find-Control $storage 'Storage check interval seconds'
    $value = $interval.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('0')
    Invoke-Button $storage 'Save storage settings'
    Wait-For {
        $all = $storage.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) { if ($item.Current.Name -like 'Settings were not saved:*') { return $true } }
    } 'invalid interval rejection' | Out-Null
    $value.SetValue('10')
    $notifications = Find-Control $storage 'Notify when a drive enters warning or critical space'
    $notifications.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Button $storage 'Save storage settings'
    Wait-For {
        $config = Read-Json (Join-Path $root 'configuration.json')
        $config.configuration.settings.storage.checkIntervalSeconds -eq 10 -and
            $config.configuration.settings.storage.notificationsEnabled -eq $false
    } 'saved storage preferences' | Out-Null
    if ($ExerciseScan) {
        $fixture = Join-Path $root 'scan-fixture'
        [IO.Directory]::CreateDirectory($fixture) | Out-Null
        $large = Join-Path $fixture 'large.bin'
        [IO.File]::WriteAllBytes($large, [byte[]]::new(1048576))
        [IO.File]::WriteAllBytes((Join-Path $fixture 'small.bin'), [byte[]]::new(42))
        $scanInput = Find-Control $storage 'Folder to analyze'
        $scanInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixture)
        Invoke-Button $storage 'Scan folder'
        $baselinePath = Join-Path $root 'Storage/latest-scan.json'
        $first = Wait-For {
            if (Test-Path -LiteralPath $baselinePath) {
                $snapshot = Read-Json $baselinePath
                if ($snapshot.Root -eq $fixture -and $snapshot.FilesMeasured -eq 2) { return $snapshot }
            }
        } 'first folder scan'
        $largeEntry = $first.Entries | Where-Object { $_.Path -eq $large }
        if ($largeEntry.LogicalBytes -ne 1048576 -or -not $first.CoverageComplete) { throw 'Incorrect fixture scan.' }
        [IO.File]::WriteAllBytes($large, [byte[]]::new(2097152))
        Wait-For { (Find-Control $storage 'Scan folder').Current.IsEnabled } 'scan ready' | Out-Null
        Invoke-Button $storage 'Scan folder'
        Wait-For {
            $snapshot = Read-Json $baselinePath
            $entry = $snapshot.Entries | Where-Object { $_.Path -eq $large }
            $entry.LogicalBytes -eq 2097152
        } 'second folder scan' | Out-Null
        Wait-For {
            $growth = Find-Control $storage 'Storage growth'
            $all = $growth.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($item in $all) { if ($item.Current.Name -like '+1*MiB') { return $true } }
        } 'rendered growth comparison' | Out-Null
        Write-Output 'PASS: folder scan measures controlled files, persists baseline and renders growth on rescan.'
    }
    $storage.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $dashboard.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $primary.Refresh()
    if ($primary.HasExited) { throw 'Closing dashboard stopped monitoring.' }
    $secondary = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $secondary.WaitForExit(10000) -or $secondary.ExitCode -ne 0) { throw 'Reopen failed.' }
    } finally { $secondary.Dispose() }
    Invoke-Button $dashboard 'Disk space monitoring…'
    $storage = Wait-For { Find-Control $dashboard 'Relight — Storage' } 'reopened storage window'
    $interval = Find-Control $storage 'Storage check interval seconds'
    if ($interval.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '10') {
        throw 'Saved interval was not restored.'
    }
    if ($ExerciseScan) {
        Wait-For {
            $input = Find-Control $storage 'Folder to analyze'
            $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $fixture
        } 'reloaded scan root' | Out-Null
    }
    Write-Output 'PASS: native readings, invalid save, interval/notification persistence, close-to-tray and reopened storage settings.'
    Write-Output "Isolated evidence directory: $root"
} catch {
    if ($null -ne $dashboard) {
        $dashboard.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
            Write-Output "$($_.Current.ControlType.ProgrammaticName): $($_.Current.Name)"
        }
    }
    throw
} finally {
    if ($null -ne $primary) {
        if (-not $primary.HasExited) { Stop-Process -Id $primary.Id -Force }
        $primary.Dispose()
    }
}
