param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [switch]$ExerciseScan,
    [switch]$ExerciseCleanup,
    [switch]$ExerciseAutomaticApproval,
    [switch]$ExerciseCleanupHistory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CleanupUiChoice {
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
'@
if ($ExerciseAutomaticApproval) { $ExerciseCleanup = $true }
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
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
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
function Choose-Native($Parent, [string]$Name) {
    $button = Find-Control $Parent $Name
    if ($null -eq $button -or $button.Current.NativeWindowHandle -eq 0) { throw "Native choice unavailable: $Name" }
    [CleanupUiChoice]::SendMessage([IntPtr]::new($button.Current.NativeWindowHandle), 0xF5,
        [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
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
    if ($ExerciseCleanup) {
        $cleanupFixture = Join-Path ([IO.Path]::GetTempPath()) ('relight-reviewed-ui-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($cleanupFixture) | Out-Null
        $oldLog = Join-Path $cleanupFixture 'disposable.log'
        [IO.File]::WriteAllText($oldLog, 'disposable UI test')
        [IO.File]::SetLastWriteTimeUtc($oldLog, [DateTime]::UtcNow.AddDays(-30))
        Invoke-Button $storage 'Reviewed cleanup…'
        $cleanup = Wait-For { Find-Control $storage 'Relight — Reviewed cleanup' } 'reviewed cleanup window'
        foreach ($field in @(@('Cleanup rule name', 'Disposable reviewed UI rule'), @('Cleanup folder', $cleanupFixture), @('Cleanup retain newest', '0'))) {
            $edit = Wait-For { $control = Find-Control $cleanup $field[0]; if ($control.Current.IsEnabled) { $control } } 'cleanup editor ready'
            $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($field[1])
        }
        Invoke-Button $cleanup 'Save reviewed rule'
        $rulesPath = Join-Path $root 'Storage/cleanup-rules.json'
        Wait-For { $book = Read-Json $rulesPath; $book.Rules.Count -eq 1 } 'saved reviewed rule' | Out-Null
        if (-not (Test-Path -LiteralPath $oldLog)) { throw 'Saving a rule deleted a file.' }
        Wait-For { (Find-Control $cleanup 'Preview cleanup').Current.IsEnabled } 'preview available' | Out-Null
        Invoke-Button $cleanup 'Preview cleanup'
        Wait-For { (Find-Control $cleanup 'Permanently delete selected…').Current.IsEnabled } 'reviewed candidates' | Out-Null
        $grid = Find-Control $cleanup 'Reviewed cleanup candidates'
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::CheckBox)
        foreach ($checkbox in $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
                throw 'Preview preselected a file for deletion.'
            }
        }
        if (-not (Test-Path -LiteralPath $oldLog)) { throw 'Preview deleted a file.' }
        if ($ExerciseAutomaticApproval) {
            foreach ($field in @(@('Automatic cleanup interval hours', '1'), @('Automatic cleanup trigger GiB', '1'))) {
                (Find-Control $cleanup $field[0]).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($field[1])
            }
            Invoke-Button $cleanup 'Approve automatic cleanup…'
            $approval = Wait-For { Find-Control $cleanup 'Approve automatic permanent cleanup?' } 'automatic approval confirmation'
            Choose-Native $approval 'Cancel'
            if ((Read-Json $rulesPath).AutomaticEnabled) { throw 'Cancel enabled automatic cleanup.' }
            Invoke-Button $cleanup 'Approve automatic cleanup…'
            $approval = Wait-For { Find-Control $cleanup 'Approve automatic permanent cleanup?' } 'second automatic approval confirmation'
            Choose-Native $approval 'OK'
            Wait-For { $book = Read-Json $rulesPath; $book.AutomaticEnabled -and $book.AutomaticApprovals.Count -eq 1 } 'saved explicit approval' | Out-Null
            if (-not (Test-Path -LiteralPath $oldLog)) { throw 'Approving caused an immediate deletion.' }
            Wait-For { (Find-Control $cleanup 'Disable all automatic cleanup').Current.IsEnabled } 'disable available' | Out-Null
            Invoke-Button $cleanup 'Disable all automatic cleanup'
            Wait-For { $book = Read-Json $rulesPath; $null -ne $book -and -not $book.AutomaticEnabled } 'persisted disable' | Out-Null
            Write-Output 'PASS: Cancel preserves off; explicit approval persists; no immediate deletion; disable-all persists.'
        }
        $cleanup.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Write-Output 'PASS: reviewed rule save and preview preserve disposable files; preview selections default off.'
    }
    if ($ExerciseCleanupHistory) {
        # Synthetic local audit evidence only; this check never dispatches cleanup or creates approvals.
        $storageData = Join-Path $root 'Storage'
        [IO.Directory]::CreateDirectory($storageData) | Out-Null
        $ruleId = [guid]::NewGuid().ToString()
        $pendingId = [guid]::NewGuid().ToString()
        $reviewedId = [guid]::NewGuid().ToString()
        $legacyId = [guid]::NewGuid().ToString()
        $now = [DateTimeOffset]::UtcNow
        $pending = @{ Id=$pendingId; RuleId=$ruleId; Origin='Automatic'; StartedUtc=$now.ToString('o'); FinishedUtc=$null;
            Items=@(@{Path='disposable-pending.log';Outcome='Pending; outcome unknown if interrupted';Detail=$null});
            AvailableBefore=100;AvailableAfter=$null;Problem=$null }
        $reviewed = @{ Id=$reviewedId; RuleId=$ruleId; Origin='Reviewed'; StartedUtc=$now.AddMinutes(-5).ToString('o');FinishedUtc=$now.AddMinutes(-4).ToString('o');
            Items=@(@{Path='disposable-skipped.log';Outcome='Skipped';Detail='File changed after preview'});
            AvailableBefore=100;AvailableAfter=80;Problem=$null }
        $summaries = @(
            @{Id=$pendingId;RuleId=$ruleId;Origin='Automatic';StartedUtc=$pending.StartedUtc;FinishedUtc=$null;Deleted=0;Skipped=0;NotCompleted=1;AvailableChangeBytes=$null;Problem=$null},
            @{Id=$reviewedId;RuleId=$ruleId;Origin='Reviewed';StartedUtc=$reviewed.StartedUtc;FinishedUtc=$reviewed.FinishedUtc;Deleted=0;Skipped=1;NotCompleted=0;AvailableChangeBytes=-20;Problem=$null},
            @{Id=$legacyId;RuleId=$ruleId;Origin='Reviewed';StartedUtc=$now.AddMinutes(-10).ToString('o');FinishedUtc=$now.AddMinutes(-9).ToString('o');Deleted=0;Skipped=0;NotCompleted=0;AvailableChangeBytes=$null;Problem=$null}
        )
        [IO.File]::WriteAllText((Join-Path $storageData 'cleanup-last-run.json'), ($pending | ConvertTo-Json -Depth 10))
        [IO.File]::WriteAllText((Join-Path $storageData 'cleanup-history.json'), (ConvertTo-Json -InputObject $summaries -Depth 10))
        [IO.File]::WriteAllText((Join-Path $storageData 'cleanup-run-details.json'), (@{SchemaVersion=1;Runs=@($reviewed)} | ConvertTo-Json -Depth 10))
        Invoke-Button $storage 'Cleanup history…'
        $historyWindow = Wait-For { Find-Control $storage 'Relight — Cleanup history' } 'cleanup history window'
        $details = Wait-For { Find-Control $historyWindow 'Cleanup selected run details' } 'history detail text'
        function Read-Details { $details.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
        Wait-For { (Read-Details) -like '*in progress or interrupted*' } 'pending outcome explanation' | Out-Null
        $grid = Find-Control $historyWindow 'Cleanup history runs'
        $rowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem)
        $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, $rowCondition)
        if ($rows.Count -ne 3) { throw "Expected 3 synthetic history rows, got $($rows.Count)" }
        $rows[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Wait-For { (Read-Details) -like '*Observed change: -20 bytes*' } 'older reviewed run details' | Out-Null
        $outcomes = Find-Control $historyWindow 'Cleanup selected file outcomes'
        Wait-For { Find-Control $outcomes 'File changed after preview' } 'per-file skip reason' | Out-Null
        $rows[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Wait-For { (Read-Details) -like '*file details were not retained*' } 'legacy detail limitation' | Out-Null
        $problems = Find-Control $historyWindow 'Problems, skips or unfinished only'
        $problems.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Wait-For { $grid.FindAll([System.Windows.Automation.TreeScope]::Children, $rowCondition).Count -eq 2 } 'problem history filter' | Out-Null
        $problems.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Invoke-Button $historyWindow 'Refresh history'
        Wait-For { (Find-Control $historyWindow 'Refresh history').Current.IsEnabled } 'history refresh complete' | Out-Null
        $historyWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        if (-not $ExerciseCleanup -and (Test-Path (Join-Path $storageData 'cleanup-rules.json'))) { throw 'Read-only history created cleanup authority.' }
        Write-Output 'PASS: native cleanup history, older run selection, pending/legacy explanations, skip details and refresh; no cleanup dispatched.'
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
