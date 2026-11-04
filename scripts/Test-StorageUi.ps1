param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [switch]$ExerciseScan,
    [switch]$ExerciseCleanup,
    [switch]$ExerciseAutomaticApproval,
    [switch]$ExerciseCleanupHistory,
    [switch]$ExerciseStorageNotifications,
    [switch]$ExerciseScanLimit,
    [switch]$ExerciseScanProgress,
    [switch]$ExerciseLongPaths,
    [switch]$ExerciseScanFailures,
    [switch]$ExerciseProtectedFailures,
    [switch]$LegacyProtectedResult,
    [switch]$ExerciseAccessRestrictions,
    [ValidateSet('Depth','Unfinished','Unknown')][string]$CoverageWarningCase
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CleanupUiChoice {
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path, uint access,
        uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    public static Microsoft.Win32.SafeHandles.SafeFileHandle HoldDirectory(string path) {
        var handle = OpenDirectory(@"\\?\" + path, 1, 0, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { int code=Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(code); }
        return handle;
    }
}
'@
if ($ExerciseAutomaticApproval) { $ExerciseCleanup = $true }
if ($ExerciseScanProgress) { $ExerciseScanLimit = $true }
if ($ExerciseScanLimit) { $ExerciseScan = $true }
if ($ExerciseLongPaths) { $ExerciseScan = $true }
if ($ExerciseScanFailures) { $ExerciseScan = $true }
if ($LegacyProtectedResult) { $ExerciseProtectedFailures = $true }
if ($ExerciseAccessRestrictions) { $ExerciseProtectedFailures = $true }
if ($CoverageWarningCase) { $ExerciseProtectedFailures = $true }
if ($ExerciseAccessRestrictions -and $LegacyProtectedResult) { throw 'Choose new access groups or the legacy fixture, not both.' }
if ($CoverageWarningCase -and ($ExerciseAccessRestrictions -or $LegacyProtectedResult)) { throw 'Choose one saved-result fixture.' }
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
    try {
        # Poll an old or new complete file without preventing the app's atomic replacement.
        $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=[IO.StreamReader]::new($stream)
        try {$json=$reader.ReadToEnd()} finally {$reader.Dispose()}
        $json | ConvertFrom-Json
    }
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
    # Saved display fixtures only. Classification is tested against fake adapters in unit tests.
    $seededBaseline = $null
    if ($ExerciseProtectedFailures) {
        $protectedFixture = Join-Path $root 'protected-fixture'
        [IO.Directory]::CreateDirectory($protectedFixture) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $root 'Storage')) | Out-Null
        $seededBaseline = Join-Path $root 'Storage/latest-scan.json'
        $now = [DateTimeOffset]::UtcNow.ToString('o')
        $seed = @{SchemaVersion=1; Root=$protectedFixture; StartedUtc=$now; FinishedUtc=$now; TraversalComplete=$true;
            EntriesVisited=4; FilesMeasured=0; SkippedEntries=3; HardLinkAliases=0; EntryLimitReached=$false; EntryLimit=100000; Entries=@();
            IssueCounts=@(@{Category=1;Count=1},@{Category=3;Count=$(if($LegacyProtectedResult){2}else{1})});
            Issues=@(@{Path=(Join-Path $protectedFixture 'protected-example');Reason='Access is denied.';Category=$(if($LegacyProtectedResult){3}else{10});Operation=1;NativeErrorCode=5;HResult=-2147467259},
                @{Path=(Join-Path $protectedFixture 'other-example');Reason='Access is denied.';Category=3;Operation=1;NativeErrorCode=5;HResult=-2147467259},
                @{Path=(Join-Path $protectedFixture 'excluded-example');Reason='Expected exclusion';Category=1;Operation=1})}
        if (-not $LegacyProtectedResult) {$seed.FailureClassificationVersion=1; $seed.IssueCounts+=@{Category=10;Count=1}}
        if ($ExerciseAccessRestrictions) {
            $folderA=Join-Path $protectedFixture 'folder-A'
            $folderB=Join-Path $protectedFixture 'folder-B'
            $seed.EntriesVisited=1000; $seed.SkippedEntries=939
            $seed.IssueCounts=@(@{Category=1;Count=1},@{Category=3;Count=935},@{Category=4;Count=1},@{Category=8;Count=1},@{Category=10;Count=1})
            $seed.AccessRestrictions=@{Version=1;Folders=@(@{Folder=$folderA;Count=900},@{Folder=$folderB;Count=30});UngroupedFailures=5}
            $seed.Issues=@(@{Path=(Join-Path $folderA 'sample');Reason='Access is denied.';Category=3;Operation=1;NativeErrorCode=5},
                @{Path=$folderB;Reason='Access is denied.';Category=3;Operation=2;NativeErrorCode=5},
                @{Path=(Join-Path $protectedFixture 'protected-example');Reason='Access is denied.';Category=10;Operation=1;NativeErrorCode=5},
                @{Path=(Join-Path $protectedFixture 'missing');Reason='Path changed';Category=4;Operation=1;NativeErrorCode=3},
                @{Path=(Join-Path $protectedFixture 'io-error');Reason='I/O error';Category=8;Operation=1;NativeErrorCode=1117},
                @{Path=(Join-Path $protectedFixture 'excluded');Reason='Expected exclusion';Category=1;Operation=1})
        }
        if ($CoverageWarningCase -eq 'Depth') {
            $seed.EntriesVisited=5; $seed.SkippedEntries=4
            $seed.IssueCounts+=@{Category=2;Count=1}
            $seed.Issues+=@{Path=(Join-Path $protectedFixture 'deep');Reason='Maximum depth reached';Category=2;Operation=2}
        } elseif ($CoverageWarningCase -eq 'Unfinished') {
            $seed.TraversalComplete=$false
        } elseif ($CoverageWarningCase -eq 'Unknown') {
            $seed.Remove('IssueCounts'); $seed.Remove('FailureClassificationVersion')
            $seed.Issues=@(@{Path=(Join-Path $protectedFixture 'old-sample');Reason='Unclassified older gap'})
        }
        $seed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $seededBaseline -Encoding utf8
        $seedHash = (Get-FileHash -LiteralPath $seededBaseline -Algorithm SHA256).Hash
    }
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
        if ($seededBaseline -and $name -eq 'latest-scan.json') {continue}
        if (Test-Path (Join-Path $root "Storage/$name")) {throw "Navigation created operational data: $name"}
    }
    $settingsNavigation = Find-Control $dashboard 'Settings'
    if ($null -eq $settingsNavigation) {$settingsNavigation = Find-Control $dashboard '_Settings'}
    $settingsNavigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if (Find-Control $dashboard 'Disk space monitoring…') {throw 'Old storage entry remains under general Settings.'}
    $navigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output 'PASS: top-level Storage and four embedded sections; navigation performs no scan/cleanup or authority writes.'
    if ($ExerciseProtectedFailures) {
        Select-StorageSection 'Space usage'
        $expectedWarning=if($CoverageWarningCase -eq 'Depth'){'SCAN LIMITED — DIRECTORY DEPTH REACHED'}
            elseif($CoverageWarningCase -eq 'Unfinished'){'SCAN UNFINISHED — COVERAGE INCOMPLETE'}
            elseif($CoverageWarningCase -eq 'Unknown' -or $LegacyProtectedResult){'SCAN COVERAGE UNKNOWN'}
            elseif($ExerciseAccessRestrictions){'SCAN COMPLETED WITH ERRORS'}else{$null}
        if($expectedWarning) {
            Wait-For {Find-Control $storage $expectedWarning} 'prominent coverage warning' | Out-Null
            if((Find-Control $storage $expectedWarning).Current.IsOffscreen){throw 'Coverage warning is offscreen.'}
        } else {
            $statusCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ScanResultStatus')
            Wait-For {$storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$statusCondition).Current.Name.Contains('Scan completed with exclusions and access restrictions.')} 'neutral completed status' | Out-Null
            $warningCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ScanCoverageWarning')
            $warningPanel=$storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$warningCondition)
            if($warningPanel -and -not $warningPanel.Current.IsOffscreen){throw 'Expected restrictions still show a prominent coverage warning.'}
            $noteCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ScanCoverageNote')
            if(-not $storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$noteCondition).Current.Name.Contains('not measured')){throw 'Neutral result hides unmeasured coverage.'}
        }
        if($CoverageWarningCase) {
            Write-Output "PASS: $CoverageWarningCase retains a prominent warning and preserved saved evidence."
        } else {
        $totals = Wait-For {
            $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ScanCoverageTotals')
            $candidate = $storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if ($candidate -and $candidate.Current.Name.Contains($(if($ExerciseAccessRestrictions){'Failed: 938'}else{'Failed: 2'}))) {return $candidate.Current.Name}
        } 'saved failure totals'
        $otherCount = (Find-Control $storage 'Scan read failures').GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern).Current.RowCount
        $folderGrid=Find-Control $storage 'Scan access-restricted folders'
        $folderPattern=$folderGrid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern)
        $summaryCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ScanAccessRestrictionSummary')
        $folderSummary=$storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$summaryCondition).Current.Name
        if ($ExerciseAccessRestrictions) {
            if (-not $totals.Contains('Protected system: 1 · Access restricted: 935 · Other errors: 2') -or $otherCount -ne 2) {throw 'Restriction/protected/other error counts overlap or differ.'}
            if ($folderPattern.Current.RowCount -ne 3 -or -not $folderSummary.Contains('5 restrictions are in Other folders')) {throw 'Exact folder counts or explicit grouping remainder missing.'}
            foreach($row in 0..2) {
                $expected=@('900','30','5')[$row]
                $cell=$folderPattern.GetItem($row,1)
                $texts=$cell.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
                $names=@($cell.Current.Name)+@($texts | ForEach-Object {$_.Current.Name})
                if ($names -notcontains $expected) {throw "Folder row $row does not display count $expected (names: $names)."}
            }
            Write-Output 'PASS: 935 restrictions grouped separately with exact 900/30/remainder 5 counts; protected and other errors stay separate; partial coverage retained.'
            $errorGrid=Find-Control $storage 'Scan read failures'
            $rows=$errorGrid.FindAll([System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::DataItem))
            $rows.Item(1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $details=Wait-For {Find-Control $storage 'Selected scan error details'} 'selected error details'
            $detailText=$details.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            foreach($expected in @('io-error','1117','Read metadata','Suggested next steps:','check storage health')) {
                if(-not $detailText.Contains($expected)){throw "Selected error lacks $expected."}
            }
            if(-not (Find-Control $storage 'Copy selected error details').Current.IsEnabled){throw 'Local copy action unavailable.'}
            if((Find-Control $storage 'Scan other-error counts and next steps').GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern).Current.RowCount -ne 2){throw 'Exact other-error counts or guidance missing.'}
            Write-Output 'PASS: native selected error shows path, operation, Windows code/message and specific device-error next steps; local copy action available.'
        } elseif ($LegacyProtectedResult) {
            if (-not $totals.Contains('Access restricted: 2') -or -not $totals.Contains('split: unknown') -or $otherCount -ne 0) {throw 'Legacy protected split was invented or access restrictions mixed into other errors.'}
            if ($folderPattern.Current.RowCount -ne 1 -or -not $folderSummary.Contains('retained samples only (2 shown)')) {throw 'Legacy folder sample counts were presented as exact full-scan counts.'}
            if (Find-Control $storage 'Protected-system access failures — samples') {throw 'Legacy result unexpectedly shows a protected classification.'}
            Write-Output 'PASS: legacy total preserved; protected split unknown; access restrictions grouped as retained samples only with partial coverage.'
        } else {
            if (-not $totals.Contains('Protected system: 1 · Access restricted: 1 · Other errors: 0') -or $otherCount -ne 0) {throw 'Protected/restricted/other totals or failure separation incorrect.'}
            if ($folderPattern.Current.RowCount -ne 1 -or -not $folderSummary.Contains('retained samples only (1 shown)')) {throw 'Older classified scan acquired invented exact folder counts.'}
            $expander = Find-Control $storage 'Protected-system access failures — samples'
            $pattern = $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {throw 'Protected samples are not initially collapsed.'}
            $pattern.Expand()
            $grid = Wait-For {Find-Control $storage 'Scan protected-system failures'} 'expanded protected samples'
            if ($grid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern).Current.RowCount -ne 1) {throw 'Protected sample missing or other failures mixed in.'}
            Write-Output 'PASS: total includes protected and access-restricted subsets; old folder counts remain sample-only; protected samples expand; partial coverage retained.'
        }
        $accessExpander=Find-Control $storage 'Access-restricted samples — first and recent'
        $accessExpander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $accessSamples=Wait-For {Find-Control $storage 'Scan access-restricted samples'} 'expanded access-restricted samples'
        $expectedSamples=if($ExerciseAccessRestrictions -or $LegacyProtectedResult){2}else{1}
        if($accessSamples.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern).Current.RowCount -ne $expectedSamples){throw 'Access-restricted samples missing or mixed with other errors.'}
        }
        if ((Get-FileHash -LiteralPath $seededBaseline -Algorithm SHA256).Hash -ne $seedHash) {throw 'Loading reclassified or rewrote saved evidence.'}
        if (@(Get-ChildItem -LiteralPath (Join-Path $root 'Logs/Diagnostics') -Filter 'scan-failures-*.jsonl' -ErrorAction SilentlyContinue).Count) {throw 'Loading saved evidence generated new failure events.'}
        Select-StorageSection 'Overview'
    }
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
    if ($ExerciseScanFailures) {
        Select-StorageSection 'Space usage'
        $blockedFolder = Join-Path $fixture 'blocked-directory'
        $junction = Join-Path $fixture 'excluded-junction'
        $outside = Join-Path $root 'excluded-target'
        [IO.Directory]::CreateDirectory($blockedFolder) | Out-Null
        [IO.Directory]::CreateDirectory($outside) | Out-Null
        & cmd.exe /c mklink /J $junction $outside | Out-Null
        if ($LASTEXITCODE -ne 0) {throw 'Disposable junction creation failed.'}
        $heldDirectory = $null
        try {
            $heldDirectory = [CleanupUiChoice]::HoldDirectory($blockedFolder)
            $prior = (Read-Json $baselinePath).FinishedUtc
            Wait-For { (Find-Control $storage 'Scan folder').Current.IsEnabled } 'scan ready for failure fixture' | Out-Null
            Invoke-Button $storage 'Scan folder'
            $partial = Wait-For {
                $next = Read-Json $baselinePath
                if ($next.FinishedUtc -ne $prior -and $next.FailedEntries -eq 1 -and $next.ExcludedEntries -eq 1) {return $next}
            } 'separate saved failure/exclusion counts'
            if ($partial.CoverageComplete) {throw 'Failure fixture incorrectly has complete coverage.'}
            Wait-For {
                $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ScanCoverageTotals')
                $text = $storage.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition).Current.Name
                $text.Contains('Excluded: 1 · Failed: 1 · Depth-limited: 0') -and $text.Contains('Protected system: 0 · Access restricted: 0 · Other errors: 1')
            } 'visible separated totals' | Out-Null
            $failuresGrid = Find-Control $storage 'Scan read failures'
            if ($failuresGrid.GetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern).Current.RowCount -ne 1) {throw 'Failure grid includes exclusions or omits failure.'}
            Wait-For {Find-Control $storage 'SCAN COMPLETED WITH ERRORS'} 'other-error warning for actual locked folder' | Out-Null
            $details=Find-Control $storage 'Selected scan error details'
            $detailText=$details.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if(-not $detailText.Contains('Windows code: 32') -or -not $detailText.Contains('Let the owning operation finish')){throw 'Sharing-error details or safe suggested action missing.'}
            Wait-For {
                $failureRecords = @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs/Diagnostics') -Filter 'scan-failures-*.jsonl' | ForEach-Object {
                    Get-Content -LiteralPath $_.FullName | ForEach-Object {try {$_ | ConvertFrom-Json} catch {}}
                })
                $summary = $failureRecords | Where-Object {$_.Entry.ScanFailures.FailedEntries -eq 1} | Select-Object -Last 1
                $summary.Entry.ScanFailures.Groups.NativeCode -contains 32
            } 'flushed failure-only summary and Windows sharing code' | Out-Null
            $rawFailures = Get-ChildItem -LiteralPath (Join-Path $root 'Logs/Diagnostics') -Filter 'scan-failures-*.jsonl' | Get-Content
            if ($rawFailures -match [regex]::Escape($fixture) -or $rawFailures -match 'ReparseOrOffline') {throw 'Failure log exposes a path or contains expected exclusions.'}
        } finally {
            if ($null -ne $heldDirectory) {$heldDirectory.Dispose()}
            [IO.Directory]::Delete($junction)
            [IO.Directory]::Delete($blockedFolder)
        }
        Wait-For { (Find-Control $storage 'Scan folder').Current.IsEnabled } 'rescan after fixture release' | Out-Null
        Invoke-Button $storage 'Scan folder'
        Wait-For { $next=Read-Json $baselinePath; $next.CoverageComplete -and $next.FailedEntries -eq 0 -and $next.ExcludedEntries -eq 0 } 'complete replacement after removing fixture gaps' | Out-Null
        Write-Output 'PASS: separate failure/exclusion totals and failure-only grid; dedicated redacted log includes native code; complete rescan clears gaps.'
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
