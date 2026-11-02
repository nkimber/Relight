param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [switch]$ExerciseScan,
    [switch]$ExerciseCleanup,
    [switch]$ExerciseAutomaticApproval,
    [switch]$ExerciseCleanupHistory,
    [switch]$ExerciseStorageNotifications,
    [switch]$ExerciseScanLimit,
    [switch]$ExerciseScanProgress,
    [switch]$ExerciseLongPaths
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
if ($ExerciseScanProgress) { $ExerciseScanLimit = $true }
if ($ExerciseScanLimit) { $ExerciseScan = $true }
if ($ExerciseLongPaths) { $ExerciseScan = $true }
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
    catch [System.Management.Automation.ItemNotFoundException] { return $null }
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
$progressFixture = $null
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
    $navigation = Wait-For {
        $candidate = Find-Control $dashboard 'Storage'
        if ($null -eq $candidate) { $candidate = Find-Control $dashboard '_Storage' }
        $candidate
    } 'top-level Storage navigation'
    $navigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $storage = Wait-For { Find-Control $dashboard 'Storage workspace' } 'embedded storage workspace'
    function Select-StorageSection([string]$Name) {
        $condition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::TabItem))
        $section = (Find-Control $storage 'Storage sections').FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
        $section.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    foreach ($section in @('Space usage','Cleanup rules','Cleanup history','Overview')) { Select-StorageSection $section }
    foreach ($name in @('latest-scan.json','cleanup-rules.json','cleanup-last-run.json','cleanup-run-details.json')) {
        if (Test-Path (Join-Path $root "Storage/$name")) {throw "Navigation created operational data: $name"}
    }
    $settingsNavigation = Find-Control $dashboard 'Settings'
    if ($null -eq $settingsNavigation) {$settingsNavigation = Find-Control $dashboard '_Settings'}
    $settingsNavigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if (Find-Control $dashboard 'Disk space monitoring…') {throw 'Old storage entry remains under general Settings.'}
    $navigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output 'PASS: top-level Storage and four embedded sections; navigation performs no scan/cleanup or authority writes.'
    $readings = Wait-For {
        $all = $storage.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) {
            if ($item.Current.Name -like '*GiB*checked*') { return $item.Current.Name }
        }
    } 'native space reading'
    Invoke-Button $storage 'Monitoring settings…'
    $preferences = Wait-For { Find-Control $dashboard 'Relight — Monitoring settings' } 'monitoring settings dialog'
    $interval = Find-Control $preferences 'Storage check interval seconds'
    $value = $interval.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('0')
    Invoke-Button $preferences 'Save storage settings'
    Wait-For {
        $all = $preferences.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $all) { if ($item.Current.Name -like 'Settings were not saved:*') { return $true } }
    } 'invalid interval rejection' | Out-Null
    $value.SetValue('10')
    $notifications = Find-Control $preferences 'Notify when a drive enters warning or critical space'
    $notifications.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Button $preferences 'Save storage settings'
    Wait-For {
        $config = Read-Json (Join-Path $root 'configuration.json')
        $config.configuration.settings.storage.checkIntervalSeconds -eq 10 -and
            $config.configuration.settings.storage.notificationsEnabled -eq $false
    } 'saved storage preferences' | Out-Null
    if ($ExerciseStorageNotifications) {
        $reminder = (Find-Control $preferences 'Storage reminder interval minutes').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $reminder.SetValue('14')
        Invoke-Button $preferences 'Save storage settings'
        Wait-For { Find-Control $preferences 'Settings were not saved: Reminder interval must be 0 (off), or 15–10080 minutes.' } 'invalid reminder rejection' | Out-Null
        $reminder.SetValue('120')
        Invoke-Button $preferences 'Save storage settings'
        Wait-For { (Read-Json (Join-Path $root 'configuration.json')).configuration.settings.storage.reminderIntervalMinutes -eq 120 } 'persisted reminder interval' | Out-Null
        $preferences.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        $snooze = (Find-Control $storage 'Storage notification snooze minutes').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $snooze.SetValue('4')
        Invoke-Button $storage 'Snooze disk-space notifications'
        Wait-For { Find-Control $storage 'Snooze was not changed: Enter 5–1440 whole minutes for snooze.' } 'invalid snooze rejection' | Out-Null
        $snooze.SetValue('60')
        Invoke-Button $storage 'Snooze disk-space notifications'
        Wait-For {
            $all = $storage.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($item in $all) { if ($item.Current.Name -like 'Disk-space notifications snoozed for*') {return $true} }
        } 'active snooze status' | Out-Null
    } else {
        $preferences.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    }
    if ($ExerciseScan) {
        Select-StorageSection 'Space usage'
        $fixture = Join-Path $root 'scan-fixture'
        if ($ExerciseLongPaths) {
            while ($fixture.Length -lt 600) { $fixture = Join-Path $fixture ('é文-' + ('x' * 60)) }
        }
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
        if (-not (Find-Control $storage 'Open diagnostic logs')) { throw 'Diagnostic log button missing.' }
        Wait-For {
            $records = @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs/Diagnostics') -Filter 'application-*.jsonl' | ForEach-Object {
                Get-Content -LiteralPath $_.FullName | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } }
            })
            $finished = @($records | Where-Object { $_.Entry.Kind -eq 'ScanFinished' -and $_.Entry.Scan.Measured -eq 2 })
            $stages = @($records | Where-Object { $_.Entry.Kind -eq 'ScanStage' } | ForEach-Object { $_.Entry.Scan.Stage })
            $finished.Count -ge 2 -and 'AwaitingUi' -in $stages -and 'Saving' -in $stages -and 'Comparing' -in $stages
        } 'flushed correlated scan diagnostics' | Out-Null
        $rawDiagnostics = Get-ChildItem -LiteralPath (Join-Path $root 'Logs/Diagnostics') -Filter 'application-*.jsonl' | Get-Content
        if ($rawDiagnostics -match [regex]::Escape($fixture)) { throw 'Diagnostic logs expose the scan root.' }
        Write-Output 'PASS: log-folder control and flushed scan completion/stages/counters; raw scan root omitted.'
        if ($ExerciseScanLimit) {
            $limitInput = (Find-Control $storage 'Scan entry limit').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $limitInput.SetValue('0'); Invoke-Button $storage 'Save scan limit'
            Wait-For { Find-Control $storage 'Scan limit was not saved: Scan entry limit must be 1–10000000 files and folders.' } 'invalid scan budget rejection' | Out-Null
            $limitInput.SetValue('2'); Invoke-Button $storage 'Save scan limit'
            Wait-For { (Read-Json (Join-Path $root 'configuration.json')).configuration.settings.storage.scanEntryLimit -eq 2 } 'persisted small scan budget' | Out-Null
            Invoke-Button $storage 'Scan folder'
            $notice = Wait-For { Find-Control $dashboard 'Scan stopped — entry limit reached' } 'unmissable scan-stop warning'
            Choose-Native $notice 'OK'
            Wait-For { Find-Control $storage 'SCAN STOPPED — ENTRY LIMIT REACHED' } 'persistent limit warning' | Out-Null
            if ((Find-Control $storage 'SCAN STOPPED — ENTRY LIMIT REACHED').Current.IsOffscreen) {throw 'Stop warning is offscreen.'}
            if (-not (Find-Control $storage 'Largest files found in PARTIAL scan (allocated space)')) {throw 'Partial rankings not labeled.'}
            $snapshot = Read-Json $baselinePath
            if (-not $snapshot.EntryLimitReached -or $snapshot.EntryLimit -ne 2) {throw 'Actual scan budget not recorded.'}
            $limitInput.SetValue('1000000'); Invoke-Button $storage 'Save scan limit'
            Wait-For { (Read-Json (Join-Path $root 'configuration.json')).configuration.settings.storage.scanEntryLimit -eq 1000000 } 'raised scan budget' | Out-Null
            if (-not (Find-Control $storage 'SCAN STOPPED — ENTRY LIMIT REACHED')) {throw 'Changing preferences hid prior partial coverage.'}
            Select-StorageSection 'Overview'; Select-StorageSection 'Space usage'
            if (-not (Find-Control $storage 'SCAN STOPPED — ENTRY LIMIT REACHED')) {throw 'Navigation hid partial coverage.'}
            if ($ExerciseScanProgress) {
                $progressFixture = Join-Path $root 'progress-fixture'
                [IO.Directory]::CreateDirectory($progressFixture) | Out-Null
                for ($i=0; $i -lt 10000; $i++) { [IO.File]::WriteAllBytes((Join-Path $progressFixture "$i.tmp"), [byte[]]::new(0)) }
                $scanInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($progressFixture)
                Invoke-Button $storage 'Scan folder'
                Wait-For { $active=Find-Control $storage 'Scan in progress'; $active -and -not $active.Current.IsOffscreen } 'prominent active scan view' | Out-Null
                foreach ($old in @('SCAN STOPPED — ENTRY LIMIT REACHED','Largest files','Storage growth')) {
                    $control = Find-Control $storage $old
                    if ($control -and -not $control.Current.IsOffscreen) {throw "Prior scan content visible during scan: $old"}
                }
                if (-not (Find-Control $storage 'Cancel scan').Current.IsEnabled) {throw 'Active scan cancellation unavailable.'}
                Wait-For { $next=Read-Json $baselinePath; $next.Root -eq $progressFixture -and $next.CoverageComplete } 'active scan completion' | Out-Null
                Wait-For { $control=Find-Control $storage 'Scan folder'; $control -and $control.Current.IsEnabled } 'idle scan controls restored' | Out-Null
                Invoke-Button $storage 'Scan folder'
                Wait-For { $active=Find-Control $storage 'Scan in progress'; $active -and -not $active.Current.IsOffscreen } 'repeat scan progress with prior counts' | Out-Null
                $priorCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PriorScanWork')
                $priorText = $storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $priorCondition).Current.Name
                if ($priorText -notlike '*file paths measured*' -or $priorText -notlike '*Complete prior coverage*') {throw 'Previous workload missing from active scan.'}
                $etaCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ScanWorkEstimate')
                $etaText = $storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $etaCondition).Current.Name
                if ($etaText -notlike '*remaining*') {throw 'Remaining-work estimate missing.'}
                $cancel = Find-Control $storage 'Cancel scan'
                if ($cancel.Current.IsOffscreen -or -not $cancel.Current.IsEnabled) {throw 'Cancel scrolled out of the active header.'}
                Wait-For { $control=Find-Control $storage 'Scan folder'; $control -and $control.Current.IsEnabled } 'repeat scan completion' | Out-Null
                Write-Output 'PASS: same-root rescan shows retained file/entry counts and estimated remaining-work text; Cancel stays visible.'
                $scanInput = Find-Control $storage 'Folder to analyze'
                $scanInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixture)
                Write-Output 'PASS: active root/progress/cancel view replaces prior warning and result tables; completion restores scan content.'
            }
            Invoke-Button $storage 'Scan folder'
            Wait-For { $next=Read-Json $baselinePath; -not $next.EntryLimitReached -and $next.EntryLimit -eq 1000000 } 'complete rescan using raised budget' | Out-Null
            Wait-For { -not (Find-Control $storage 'SCAN STOPPED — ENTRY LIMIT REACHED') } 'limit warning cleared only by rescan' | Out-Null
            Write-Output 'PASS: saved scan budgets, pinned stop banner, acknowledgement, partial headings and warning retained until rescan.'
        }
    }
    if ($ExerciseCleanup) {
        $cleanupFixture = Join-Path ([IO.Path]::GetTempPath()) ('relight-reviewed-ui-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($cleanupFixture) | Out-Null
        $oldLog = Join-Path $cleanupFixture 'disposable.log'
        [IO.File]::WriteAllText($oldLog, 'disposable UI test')
        [IO.File]::SetLastWriteTimeUtc($oldLog, [DateTime]::UtcNow.AddDays(-30))
        Select-StorageSection 'Cleanup rules'
        $cleanup = $storage
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
            $approval = Wait-For { Find-Control $dashboard 'Approve automatic permanent cleanup?' } 'automatic approval confirmation'
            Choose-Native $approval 'Cancel'
            if ((Read-Json $rulesPath).AutomaticEnabled) { throw 'Cancel enabled automatic cleanup.' }
            Invoke-Button $cleanup 'Approve automatic cleanup…'
            $approval = Wait-For { Find-Control $dashboard 'Approve automatic permanent cleanup?' } 'second automatic approval confirmation'
            Choose-Native $approval 'OK'
            Wait-For { $book = Read-Json $rulesPath; $book.AutomaticEnabled -and $book.AutomaticApprovals.Count -eq 1 } 'saved explicit approval' | Out-Null
            if (-not (Test-Path -LiteralPath $oldLog)) { throw 'Approving caused an immediate deletion.' }
            Wait-For { (Find-Control $cleanup 'Disable all automatic cleanup').Current.IsEnabled } 'disable available' | Out-Null
            Invoke-Button $cleanup 'Disable all automatic cleanup'
            Wait-For { $book = Read-Json $rulesPath; $null -ne $book -and -not $book.AutomaticEnabled } 'persisted disable' | Out-Null
            Write-Output 'PASS: Cancel preserves off; explicit approval persists; no immediate deletion; disable-all persists.'
        }
        Select-StorageSection 'Overview'
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
        Select-StorageSection 'Cleanup history'
        $historyWindow = $storage
        $details = Wait-For { Find-Control $historyWindow 'Cleanup selected run details' } 'history detail text'
        function Read-Details { $details.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
        Wait-For { (Read-Details) -like '*in progress or interrupted*' } 'pending outcome explanation' | Out-Null
        $grid = Find-Control $historyWindow 'Cleanup history runs'
        $rowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem)
        $gridPattern = $grid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern)
        if ($gridPattern.Current.RowCount -ne 3) { throw "Expected 3 synthetic history rows, got $($gridPattern.Current.RowCount)" }
        function Select-HistoryRow([int]$Index) {
            $item = $gridPattern.GetItem($Index, 0)
            while ($item.Current.ControlType -ne [System.Windows.Automation.ControlType]::DataItem) {
                $item = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
            }
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        Select-HistoryRow 1
        Wait-For { (Read-Details) -like '*Observed change: -20 bytes*' } 'older reviewed run details' | Out-Null
        $outcomes = Find-Control $historyWindow 'Cleanup selected file outcomes'
        Wait-For { Find-Control $outcomes 'File changed after preview' } 'per-file skip reason' | Out-Null
        Select-HistoryRow 2
        Wait-For { (Read-Details) -like '*file details were not retained*' } 'legacy detail limitation' | Out-Null
        $problems = Find-Control $historyWindow 'Problems, skips or unfinished only'
        $problems.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Wait-For { $gridPattern.Current.RowCount -eq 2 } 'problem history filter' | Out-Null
        $problems.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Invoke-Button $historyWindow 'Refresh history'
        Wait-For { (Find-Control $historyWindow 'Refresh history').Current.IsEnabled } 'history refresh complete' | Out-Null
        Select-StorageSection 'Overview'
        if (-not $ExerciseCleanup -and (Test-Path (Join-Path $storageData 'cleanup-rules.json'))) { throw 'Read-only history created cleanup authority.' }
        Write-Output 'PASS: native cleanup history, older run selection, pending/legacy explanations, skip details and refresh; no cleanup dispatched.'
    }
    $dashboard.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $primary.Refresh()
    if ($primary.HasExited) { throw 'Closing dashboard stopped monitoring.' }
    $secondary = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $secondary.WaitForExit(10000) -or $secondary.ExitCode -ne 0) { throw 'Reopen failed.' }
    } finally { $secondary.Dispose() }
    $navigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Select-StorageSection 'Overview'
    Invoke-Button $storage 'Monitoring settings…'
    $preferences = Wait-For { Find-Control $dashboard 'Relight — Monitoring settings' } 'reopened monitoring settings'
    $interval = Find-Control $preferences 'Storage check interval seconds'
    if ($interval.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '10') {
        throw 'Saved interval was not restored.'
    }
    if ($ExerciseScanLimit) {
        $savedBudget = (Find-Control $preferences 'Maximum scan entries').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($savedBudget -ne '1000000') {throw 'Saved scan budget was not restored in monitoring settings.'}
        Invoke-Button $preferences 'Save storage settings'
        Wait-For { (Read-Json (Join-Path $root 'configuration.json')).configuration.settings.storage.scanEntryLimit -eq 1000000 } 'monitoring save preserves scan budget' | Out-Null
    }
    if ($ExerciseStorageNotifications) {
        $reminder = (Find-Control $preferences 'Storage reminder interval minutes').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($reminder.Current.Value -ne '120') { throw 'Reminder interval was not restored.' }
        $resume = Find-Control $storage 'Resume disk-space notifications'
        Wait-For { $resume.Current.IsEnabled } 'snooze survives storage/dashboard closure' | Out-Null
        Invoke-Button $storage 'Resume disk-space notifications'
        Wait-For { -not $resume.Current.IsEnabled } 'notification resume' | Out-Null
        if (-not $ExerciseCleanup -and (Test-Path (Join-Path $root 'Storage/cleanup-rules.json'))) {throw 'Notification settings created cleanup authority.'}
        Write-Output 'PASS: reminder validation/persistence; snooze validation, survives window closure, and resume; no cleanup authority.'
    }
    $preferences.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if ($ExerciseScan) {
        Select-StorageSection 'Space usage'
        Wait-For {
            $input = Find-Control $storage 'Folder to analyze'
            $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $fixture
        } 'reloaded scan root' | Out-Null
        if ($ExerciseLongPaths) { Write-Output 'PASS: scan, growth and reload use a Unicode folder path longer than 600 characters.' }
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
    if ($progressFixture -and (Test-Path -LiteralPath $progressFixture)) {
        $ownedProgress = [IO.Path]::GetFullPath($progressFixture)
        $ownedParent = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
        if (-not $ownedProgress.StartsWith($ownedParent, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($ownedProgress) -ne 'progress-fixture') { throw 'Disposable progress cleanup escaped its fixture root.' }
        Remove-Item -LiteralPath $ownedProgress -Recurse -Force
    }
}
