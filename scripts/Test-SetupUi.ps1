param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [string]$Target = (Join-Path $PSScriptRoot '..\tests\Relight.TestTarget\bin\Release\net10.0-windows\Relight.TestTarget.exe'),
    [switch]$RegisterSelectedChatGpt,
    [switch]$VerifyInitialStartConfirmation,
    [switch]$AcceptInitialStart,
    [switch]$EditSavedExecutable,
    [switch]$TestSavedLaunch,
    [switch]$ExercisePauseResume,
    [switch]$ExerciseDisableRemove,
    [switch]$ExerciseExit,
    [switch]$ExerciseResetDuplicate,
    [switch]$ExerciseNoRearm,
    [switch]$ExerciseStartNow,
    [switch]$ExerciseExitRace,
    [switch]$ExerciseAmbiguity,
    [switch]$ZeroAutomaticAttempts,
    [switch]$ExerciseHistoryNavigation,
    [switch]$ExerciseBrokenConfig,
    [ValidateSet('Pause', 'Disable', 'Remove', 'Exit')]
    [string]$CancelPendingAction = '',
    [ValidateSet('StopGraceful', 'StopForceDecline', 'StopForceAccept',
        'RestartGraceful', 'RestartForceDecline', 'RestartForceAccept')]
    [string]$ExplicitAction = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RelightUiNative {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
'@

function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 12) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $until) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Description."
}

function Find-Control($Parent, [string]$Name, $Type) {
    $matchName = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $matchType = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)
    $condition = [System.Windows.Automation.AndCondition]::new($matchName, $matchType)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Button($Parent, [string]$Name) {
    $button = Find-Control $Parent $Name ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $button) { throw "Button '$Name' is unavailable." }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Find-EnabledButton($Parent, [string]$Name) {
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $condition = [System.Windows.Automation.AndCondition]::new($nameCondition,
        $typeCondition)
    foreach ($button in $Parent.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($button.Current.IsEnabled) { return $button }
    }
    return $null
}

function Read-Configuration([string]$Path) {
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch [IO.IOException] { return $null }
    catch [UnauthorizedAccessException] { return $null }
}

function Get-ProfileEvents([string]$Directory, [string]$ProfileId) {
    $logs = Join-Path $Directory 'Logs'
    if (-not (Test-Path -LiteralPath $logs)) { return @() }
    $events = @()
    foreach ($log in @(Get-ChildItem -LiteralPath $logs -File -Filter 'events-*.jsonl')) {
        foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
            if (-not $line.Contains($ProfileId)) { continue }
            try {
                $event = $line | ConvertFrom-Json
                if ($event.profileId -eq $ProfileId) { $events += $event }
            }
            catch [System.ArgumentException] { }
        }
    }
    return $events
}

function Find-MessageBox($Dashboard, [string]$Title, [int]$Seconds = 12) {
    Wait-For {
        $nested = Find-Control $Dashboard $Title `
            ([System.Windows.Automation.ControlType]::Window)
        if ($null -ne $nested) { return $nested }
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
    } $Title $Seconds
}

function Click-NativeMessageChoice($MessageBox, [string]$Choice) {
    $choiceElement = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Choice)
        $MessageBox.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    } "$Choice button"
    $className = [Text.StringBuilder]::new(128)
    [RelightUiNative]::GetClassName([IntPtr]::new($choiceElement.Current.NativeWindowHandle),
        $className, $className.Capacity) | Out-Null
    if ($className.ToString() -ne 'Button') {
        throw "The message box's $Choice control has unexpected native class $className."
    }
    [RelightUiNative]::SendMessage([IntPtr]::new($choiceElement.Current.NativeWindowHandle),
        0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}

function Complete-InitialStartWarning($Dashboard, [string]$Choice) {
    $warning = Find-MessageBox $Dashboard 'Enable automatic start?'
    Click-NativeMessageChoice $warning $Choice
}

if ($VerifyInitialStartConfirmation -and $AcceptInitialStart) {
    throw 'Choose either declining or accepting the initial-start warning for one run.'
}
if (($ExercisePauseResume -or $ExerciseDisableRemove -or $ExerciseExit -or
    $ExerciseResetDuplicate -or $ExerciseNoRearm -or $ExerciseHistoryNavigation) -and
    -not $AcceptInitialStart) {
    throw 'Lifecycle acceptance requires an automatically started disposable target.'
}
if ($ExplicitAction -and (-not $AcceptInitialStart -or $ExercisePauseResume -or
    $ExerciseDisableRemove -or $ExerciseExit -or $ExerciseResetDuplicate -or
    $ExerciseStartNow -or $ExerciseHistoryNavigation -or
    $CancelPendingAction -or
    $EditSavedExecutable -or $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'Explicit-control acceptance uses only accepted initial start and one control action.'
}
if ($CancelPendingAction -and (-not $AcceptInitialStart -or $ExercisePauseResume -or
    $ExerciseDisableRemove -or $ExerciseExit -or $ExerciseStartNow -or
    $ExerciseResetDuplicate -or $ExerciseHistoryNavigation -or
    $EditSavedExecutable -or
    $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'Pending-dispatch acceptance uses only accepted initial start and one cancellation action.'
}
if ($ExerciseStartNow -and ($AcceptInitialStart -or $ExercisePauseResume -or
    $ExerciseDisableRemove -or $ExerciseExit -or $ExerciseResetDuplicate -or
    $EditSavedExecutable -or $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'Start now acceptance uses a saved initially absent disposable target with automatic initial start off.'
}
if (($ExerciseExitRace -or $ExerciseAmbiguity) -and
    ($ExerciseExitRace -and $ExerciseAmbiguity -or $AcceptInitialStart -or
    $ExerciseStartNow -or
    $ExercisePauseResume -or $ExerciseDisableRemove -or $ExerciseExit -or
    $ExerciseResetDuplicate -or $ExerciseNoRearm -or $ExerciseHistoryNavigation -or
    $ExplicitAction -or $CancelPendingAction -or $EditSavedExecutable -or
    $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'Exit-race or ambiguity acceptance uses only one initially absent disposable profile.'
}
if ($ZeroAutomaticAttempts -and -not $ExerciseStartNow) {
    throw 'The zero-attempt UI check requires -ExerciseStartNow.'
}
if ($ExerciseHistoryNavigation -and ($ExercisePauseResume -or $ExerciseDisableRemove -or
    $ExerciseExit -or $ExerciseResetDuplicate -or $EditSavedExecutable -or
    $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'History navigation acceptance uses one automatically started disposable profile.'
}
if ($ExerciseBrokenConfig -and (-not $AcceptInitialStart -or $ExercisePauseResume -or
    $ExerciseDisableRemove -or $ExerciseExit -or $ExerciseResetDuplicate -or
    $ExerciseNoRearm -or $ExerciseStartNow -or $ExerciseHistoryNavigation -or
    $CancelPendingAction -or $ExplicitAction -or $EditSavedExecutable -or
    $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'Broken-configuration acceptance uses only accepted initial start and one disposable profile.'
}
if ($ExerciseNoRearm -and ($ExercisePauseResume -or $ExerciseDisableRemove -or
    $ExerciseExit -or $ExerciseResetDuplicate -or $ExerciseStartNow -or
    $ExerciseHistoryNavigation -or $CancelPendingAction -or $ExplicitAction -or
    $EditSavedExecutable -or $TestSavedLaunch -or $RegisterSelectedChatGpt)) {
    throw 'No-rearm acceptance uses only accepted initial start and one disposable profile.'
}

$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$targetPath = (Resolve-Path -LiteralPath $Target).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('relight-setup-ui-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$primary = $null
$secondary = $null
$targetPid = $null
$external = $null
$external2 = $null
try {
    $arguments = @('--shared-session-preview', $root, '--tray')
    $primary = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Wait-For { Test-Path -LiteralPath (Join-Path $root 'configuration.json') } 'preview configuration' | Out-Null
    $secondary = Start-Process -FilePath $executablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $secondary.WaitForExit(10000) -or $secondary.ExitCode -ne 0) {
        throw 'Second launch did not activate the primary preview.'
    }
    $dashboard = Wait-For {
        $primary.Refresh()
        if ($primary.HasExited -or $primary.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
        $candidate = [System.Windows.Automation.AutomationElement]::FromHandle($primary.MainWindowHandle)
        if ($candidate.Current.Name -eq 'Relight dashboard') { return $candidate }
        return $null
    } 'dashboard'
    if ($dashboard.Current.Name -ne 'Relight dashboard') { throw 'Dashboard has no expected accessible name.' }

    Invoke-Button $dashboard '+ Add application'
    $dialog = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Add application · Relight')
        $dashboard.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    } 'Add application dialog'
    foreach ($field in @(@('Application name', 'Disposable UI target'),
                          @('Executable file path', $targetPath))) {
        $edit = Find-Control $dialog $field[0] ([System.Windows.Automation.ControlType]::Edit)
        if ($null -eq $edit) { throw "Field '$($field[0])' has no accessible edit control." }
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($field[1])
    }
    $ready = Join-Path $root 'target.ready'
    $label = 'relight-ui-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $argumentsInput = Find-Control $dialog 'Launch arguments, one per line' `
        ([System.Windows.Automation.ControlType]::Edit)
    if ($null -eq $argumentsInput) { throw 'Launch arguments field is unavailable.' }
    $targetArguments = @('--label', $label, '--ready-file', $ready,
        '--exit-after-ms', $(if ($ExerciseNoRearm) { '100' } else { '180000' }))
    if (-not $ExplicitAction) { $targetArguments += '--hidden' }
    if ($ExplicitAction -like '*Force*') { $targetArguments += '--block-close' }
    $argumentsInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
        ($targetArguments -join "`n"))
    Invoke-Button $dialog 'Detect now'
    $result = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'No matching application is running in this session. Relight will wait for your first launch.')
        $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    } 'absent-target inspection result'
    if ($null -eq $result) { throw 'Target inspection did not report absence.' }
    $initialStart = Find-Control $dialog 'Start automatically if initially absent' `
        ([System.Windows.Automation.ControlType]::CheckBox)
    if ($null -eq $initialStart) { throw 'Initial-start choice has no accessible checkbox.' }
    $toggle = $initialStart.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw 'Initial automatic start did not default off.'
    }
    $expectedInitialStart = [bool]$AcceptInitialStart
    if ($VerifyInitialStartConfirmation -or $AcceptInitialStart) {
        $toggle.Toggle()
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            throw 'Initial automatic start could not be selected.'
        }
        Invoke-Button $dialog 'Add and protect'
        Complete-InitialStartWarning $dashboard $(if ($AcceptInitialStart) { 'Yes' } else { 'No' })
        if ($VerifyInitialStartConfirmation) {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config -or @($config.configuration.profiles).Count -ne 0) {
                throw 'Declining automatic start still registered a profile.'
            }
            $toggle.Toggle()
            if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
                throw 'Initial automatic start did not return to off.'
            }
            Write-Output 'PASS: declining the automatic-start warning left the profile unregistered.'
        }
    }
    if (-not $AcceptInitialStart) { Invoke-Button $dialog 'Add and protect' }
    $profile = Wait-For {
        $config = Read-Configuration (Join-Path $root 'configuration.json')
        if ($null -eq $config) { return $null }
        @($config.configuration.profiles | Where-Object name -eq 'Disposable UI target') | Select-Object -First 1
    } 'saved disposable profile' 45
    if ($profile.target.kind -ne 'Executable' -or
        $profile.target.identity -ne $targetPath -or
        $profile.policy.startAutomaticallyWhenInitiallyAbsent -ne $expectedInitialStart) {
        throw 'Saved profile does not match the inspected target and selected policy.'
    }
    if ((Find-Control $dashboard 'Disposable UI target' ([System.Windows.Automation.ControlType]::Text)) -eq $null) {
        Wait-For {
            Find-Control $dashboard 'Disposable UI target' ([System.Windows.Automation.ControlType]::Text)
        } 'new dashboard row' | Out-Null
    }
    if ($ExerciseExitRace -or $ExerciseAmbiguity) {
        Invoke-Button $dashboard 'Edit policy'
        $editor = Wait-For {
            Find-Control $dashboard 'Edit protection · Relight' `
                ([System.Windows.Automation.ControlType]::Window)
        } 'profile editor for controlled detection'
        $fields = ,@('Normal check interval in seconds', '60')
        if ($ExerciseExitRace) {
            $fields += ,@('Stable observation period in minutes', '1')
            $fields += ,@('Retry delay in seconds', '10')
        }
        foreach ($field in $fields) {
            $input = Find-Control $editor $field[0] `
                ([System.Windows.Automation.ControlType]::Edit)
            if ($null -eq $input) { throw "Policy editor lacks '$($field[0])'." }
            $input.GetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern).SetValue($field[1])
        }
        Invoke-Button $editor 'Save changes'
        Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            $saved = @($config.configuration.profiles | Where-Object id -eq $profile.id) |
                Select-Object -First 1
            $null -ne $saved -and $saved.policy.normalPollInterval -eq '00:01:00' -and
                (-not $ExerciseExitRace -or
                    ($saved.policy.observationPeriod -eq '00:01:00' -and
                     $saved.policy.retryDelay -eq '00:00:10'))
        } 'saved controlled-detection policy' 45 | Out-Null
    }
    if ($ExerciseNoRearm) {
        Invoke-Button $dashboard 'Edit policy'
        $editor = Wait-For {
            Find-Control $dashboard 'Edit protection · Relight' `
                ([System.Windows.Automation.ControlType]::Window)
        } 'profile editor for disabled automatic rearm'
        $attemptInput = Find-Control $editor 'Automatic attempt limit' `
            ([System.Windows.Automation.ControlType]::Edit)
        $observationInput = Find-Control $editor 'Stable observation period in minutes' `
            ([System.Windows.Automation.ControlType]::Edit)
        $appearanceInput = Find-Control $editor 'Launch appearance timeout in seconds' `
            ([System.Windows.Automation.ControlType]::Edit)
        $lockoutInput = Find-Control $editor 'Lockout discovery interval in seconds' `
            ([System.Windows.Automation.ControlType]::Edit)
        $rearmInput = Find-Control $editor 'Rearm after a stable external start' `
            ([System.Windows.Automation.ControlType]::CheckBox)
        if ($null -eq $attemptInput -or $null -eq $observationInput -or
            $null -eq $appearanceInput -or $null -eq $lockoutInput -or
            $null -eq $rearmInput) {
            throw 'Profile editor does not expose the required recovery controls.'
        }
        $attemptInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue('1')
        $observationInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue('1')
        $appearanceInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue('5')
        $lockoutInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue('5')
        $rearmToggle = $rearmInput.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        if ($rearmToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            throw 'Automatic rearm did not default on.'
        }
        $rearmToggle.Toggle()
        Invoke-Button $editor 'Save changes'
        Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            $saved = @($config.configuration.profiles | Where-Object id -eq $profile.id) |
                Select-Object -First 1
            $null -ne $saved -and $saved.policy.maximumAutomaticAttempts -eq 1 -and
                $saved.policy.observationPeriod -eq '00:01:00' -and
                $saved.policy.appearanceTimeout -eq '00:00:05' -and
                $saved.policy.lockoutDiscoveryInterval -eq '00:00:05' -and
                $saved.policy.rearmAfterStableExternalStart -eq $false
        } 'saved disabled-rearm policy' 45 | Out-Null
    }
    if ($CancelPendingAction) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $pendingBudget = Wait-For { Read-Configuration $budgetPath } 'pending-start budget'
        if ($pendingBudget.budget.reservedAutomaticAttempts -ne 0) {
            throw 'An automatic attempt was charged before the pending-dispatch action.'
        }
        if (Test-Path -LiteralPath $ready) {
            throw 'The target started before the pending-dispatch action.'
        }
        switch ($CancelPendingAction) {
            'Pause' { Invoke-Button $dashboard 'Pause protection' }
            'Disable' { Invoke-Button $dashboard 'Disable protection' }
            'Remove' {
                Invoke-Button $dashboard 'Remove profile'
                Click-NativeMessageChoice (Find-MessageBox $dashboard 'Remove profile?') 'OK'
            }
            'Exit' {
                Invoke-Button $dashboard 'Exit Relight'
                Click-NativeMessageChoice (Find-MessageBox $dashboard 'Exit Relight?') 'OK'
                Wait-For { $primary.Refresh(); $primary.HasExited } 'pending-dispatch Relight exit' 20 | Out-Null
            }
        }
        if ($CancelPendingAction -ne 'Exit') {
            Wait-For {
                $config = Read-Configuration (Join-Path $root 'configuration.json')
                $state = Read-Configuration $budgetPath
                if ($null -eq $config -or $null -eq $state) { return $false }
                if ($CancelPendingAction -eq 'Remove') {
                    return @($config.configuration.profiles | Where-Object id -eq $profile.id).Count -eq 0
                }
                if ($CancelPendingAction -eq 'Disable') {
                    return @($config.configuration.profiles | Where-Object {
                        $_.id -eq $profile.id -and $_.enabled -eq $false }).Count -eq 1
                }
                $button = Find-Control $dashboard 'Resume protection' `
                    ([System.Windows.Automation.ControlType]::Button)
                return $null -ne $button -and $button.Current.IsEnabled
            } "$CancelPendingAction configuration" | Out-Null
        }
        if (Test-Path -LiteralPath $ready) {
            throw "The target started while applying $CancelPendingAction."
        }
        Start-Sleep -Seconds 45
        if (Test-Path -LiteralPath $ready) {
            throw "$CancelPendingAction failed to cancel the pending automatic dispatch."
        }
        $budget = Read-Configuration $budgetPath
        if ($null -eq $budget -or $budget.budget.reservedAutomaticAttempts -ne 0) {
            throw "$CancelPendingAction charged an automatic attempt despite canceling dispatch."
        }
        Write-Output "PASS: $CancelPendingAction canceled the pending WPF automatic start; no target appeared or automatic attempt was charged after the retry deadline."
        return
    }
    if ($AcceptInitialStart) {
        Wait-For { Test-Path -LiteralPath $ready } 'automatic initial target start' 75 | Out-Null
        $targetPid = [int](Get-Content -LiteralPath $ready -Raw).Split('|')[0]
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $budget = Wait-For {
            $saved = Read-Configuration $budgetPath
            if ($null -ne $saved -and $saved.budget.reservedAutomaticAttempts -eq 1) { return $saved }
            return $null
        } 'durably reserved automatic attempt'
        Write-Output "PASS: accepted initial-start warning launched only disposable PID $targetPid with one durable automatic attempt."
    }
    else {
        Write-Output 'PASS: Add dialog was accessible; detection found absence; saved profile kept automatic initial start off; dashboard showed the new profile.'
    }

    if ($ExerciseBrokenConfig) {
        $configurationPath = Join-Path $root 'configuration.json'
        Wait-For { Test-Path -LiteralPath ($configurationPath + '.bak') } 'last-good backup' | Out-Null
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File -Filter 'events-*.jsonl')) {
                if (Select-String -LiteralPath $log.FullName -SimpleMatch '"kind":"TargetObserved"' -Quiet) {
                    return $true
                }
            }
            return $false
        } 'target observation' 20 | Out-Null
        Wait-For {
            try { [IO.File]::WriteAllText($configurationPath, 'invalid configuration'); return $true }
            catch [IO.IOException] { return $false }
        } 'external configuration edit' | Out-Null
        Wait-For {
            foreach ($item in $dashboard.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($item.Current.Name -like 'Configuration needs attention:*') { return $true }
            }
            return $false
        } 'rendered configuration warning' 20 | Out-Null
        $settings = Find-Control $dashboard 'Settings' ([System.Windows.Automation.ControlType]::RadioButton)
        if ($null -eq $settings) { throw 'Settings navigation is unavailable.' }
        $settings.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $repair = Wait-For {
            Find-EnabledButton $dashboard 'Restore last-good configuration'
        } 'enabled last-good repair action' 15
        $repair.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $confirmation = Find-MessageBox $dashboard 'Restore last-good configuration?'
        Click-NativeMessageChoice $confirmation 'OK'
        $complete = Find-MessageBox $dashboard 'Configuration restored'
        Click-NativeMessageChoice $complete 'OK'
        Wait-For {
            $saved = Read-Configuration $configurationPath
            if ($null -ne $saved -and $null -ne $saved.configuration) { return $saved }
            return $null
        } 'restored configuration' 20 | Out-Null
        Wait-For {
            foreach ($item in $dashboard.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($item.Current.Name -like 'Configuration needs attention:*') { return $false }
            }
            return $true
        } 'cleared configuration warning' 20 | Out-Null
        if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) {
            throw 'Configuration repair stopped the target.'
        }
        $budget = Read-Configuration $budgetPath
        if ($null -eq $budget -or $budget.budget.reservedAutomaticAttempts -ne 1) {
            throw 'Configuration repair changed the durable automatic attempt budget.'
        }
        $archives = @(Get-ChildItem -LiteralPath $root -File -Filter 'configuration-invalid-*.json')
        if ($archives.Count -ne 1) { throw 'Repair did not preserve exactly one invalid configuration.' }
        Write-Output 'PASS: invalid external edit showed WPF warning and enabled last-good repair; repair preserved invalid bytes, target and automatic budget.'
        return
    }

    if ($ExerciseNoRearm) {
        Wait-For {
            $budget = Read-Configuration $budgetPath
            $null -ne $budget -and $budget.budget.lockedOut -eq $true -and
                $budget.budget.reservedAutomaticAttempts -eq 1
        } 'one-attempt lockout' 35 | Out-Null
        $externalReady = Join-Path $root 'external.ready'
        $external = Start-Process -FilePath $targetPath -WindowStyle Hidden -PassThru `
            -ArgumentList @('--label', $label, '--ready-file', $externalReady,
                '--exit-after-ms', '180000', '--hidden')
        Wait-For { Test-Path -LiteralPath $externalReady } 'external target start' 10 | Out-Null
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"TargetObserved"') -and
                        $line.Contains($profile.id) -and
                        $line.Contains('"origin":"ExternalStart"')) { return $true }
                }
            }
            return $false
        } 'external target observation' 20 | Out-Null
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"ObservationCompleted"') -and
                        $line.Contains($profile.id) -and
                        $line.Contains('"origin":"ExternalStart"')) { return $true }
                }
            }
            return $false
        } 'stable external observation' 80 | Out-Null
        $locked = Read-Configuration $budgetPath
        if ($null -eq $locked -or $locked.budget.lockedOut -ne $true -or
            $locked.budget.reservedAutomaticAttempts -ne 1) {
            throw 'Stable external observation rearmed the disabled-rearm profile.'
        }
        if ($external.HasExited) { throw 'External target exited before recovery reset.' }
        $dispatchesBeforeReset = @(
            Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File -Filter 'events-*.jsonl' |
                Get-Content | Where-Object {
                    $_.Contains('"kind":"LaunchDispatched"') -and $_.Contains($profile.id)
                }).Count
        if ($dispatchesBeforeReset -ne 1) { throw 'Unexpected automatic dispatch after lockout.' }
        Wait-For { Find-EnabledButton $dashboard 'Reset recovery' } `
            'enabled recovery reset' 15 | Out-Null
        Invoke-Button $dashboard 'Reset recovery'
        Click-NativeMessageChoice (Find-MessageBox $dashboard 'Reset recovery?') 'OK'
        Wait-For {
            $budget = Read-Configuration $budgetPath
            $null -ne $budget -and $budget.budget.lockedOut -eq $false -and
                $budget.budget.reservedAutomaticAttempts -eq 0
        } 'explicit rearm after reset' 20 | Out-Null
        if ($external.HasExited) { throw 'Reset recovery stopped the external target.' }
        Write-Output 'PASS: stable external start remained locked out with automatic rearm disabled; WPF Reset recovery alone cleared the charged budget and preserved the target.'
    }

    if ($ExerciseStartNow) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        if ($ZeroAutomaticAttempts) {
            Invoke-Button $dashboard 'Edit policy'
            $editor = Wait-For {
                Find-Control $dashboard 'Edit protection · Relight' `
                    ([System.Windows.Automation.ControlType]::Window)
            } 'profile editor for zero-attempt policy'
            $attemptInput = Find-Control $editor 'Automatic attempt limit' `
                ([System.Windows.Automation.ControlType]::Edit)
            if ($null -eq $attemptInput) {
                throw 'Profile editor has no accessible automatic attempt limit.'
            }
            $attemptInput.GetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern).SetValue('0')
            Invoke-Button $editor 'Save changes'
            Wait-For {
                $config = Read-Configuration (Join-Path $root 'configuration.json')
                $saved = @($config.configuration.profiles | Where-Object id -eq $profile.id) |
                    Select-Object -First 1
                $null -ne $saved -and $saved.policy.maximumAutomaticAttempts -eq 0
            } 'saved zero-attempt policy' 45 | Out-Null
        }
        $beforeBudget = Wait-For { Read-Configuration $budgetPath } 'budget before explicit start'
        if ($beforeBudget.budget.reservedAutomaticAttempts -ne 0 -or
            (Test-Path -LiteralPath $ready)) {
            throw 'Start now test did not begin with an absent target and zero attempts.'
        }
        Wait-For { Find-EnabledButton $dashboard 'Start now' } 'enabled Start now action' 20 | Out-Null
        (Find-EnabledButton $dashboard 'Start now').GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-For { Test-Path -LiteralPath $ready } 'explicitly started disposable target' 20 | Out-Null
        $targetPid = [int](Get-Content -LiteralPath $ready -Raw).Split('|')[0]
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"TargetObserved"') -and
                        $line.Contains($profile.id)) { return $true }
                }
            }
            return $false
        } 'explicit target discovery' 70 | Out-Null
        Wait-For {
            $button = Find-Control $dashboard 'Start now' `
                ([System.Windows.Automation.ControlType]::Button)
            $null -ne $button -and -not $button.Current.IsEnabled
        } 'Start now disabled for present target' 15 | Out-Null
        $afterBudget = Read-Configuration $budgetPath
        if ($null -eq $afterBudget -or
            $afterBudget.budget.reservedAutomaticAttempts -ne 0) {
            throw 'Explicit Start now charged an automatic recovery attempt.'
        }
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"ExplicitStartDispatched"') -and
                        $line.Contains($profile.id)) { return $true }
                }
            }
            return $false
        } 'explicit-start history event' | Out-Null
        $policyDescription = if ($ZeroAutomaticAttempts) { ' with a zero automatic-attempt limit' } else { '' }
        Write-Output "PASS: WPF Start now$policyDescription launched the absent disposable target once, disabled duplicate start while present, logged an explicit dispatch and left the automatic budget at zero."
    }

    if ($ExerciseExitRace) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $external = Start-Process -FilePath $targetPath -WindowStyle Hidden -PassThru `
            -ArgumentList @('--label', $label, '--ready-file', $ready,
                '--exit-after-ms', '300000', '--hidden')
        Wait-For { Test-Path -LiteralPath $ready } 'external exit-race target' 10 | Out-Null
        Wait-For {
            @(Get-ProfileEvents $root $profile.id | Where-Object kind -eq 'ObservationCompleted').Count -ge 1
        } 'stable external target observation' 150 | Out-Null
        $before = Read-Configuration $budgetPath
        if ($null -eq $before -or $before.budget.reservedAutomaticAttempts -ne 0) {
            throw 'Exit-race target had a charged budget before exit.'
        }
        if ($external.HasExited) { throw 'External exit-race target closed before controlled exit.' }
        Stop-Process -Id $external.Id
        $external.WaitForExit(10000) | Out-Null
        Remove-Item -LiteralPath $ready -ErrorAction SilentlyContinue
        Wait-For {
            @(Get-ProfileEvents $root $profile.id | Where-Object kind -eq 'TargetDisappeared').Count -ge 1
        } 'callback-driven target disappearance' 20 | Out-Null
        $button = Wait-For {
            Find-EnabledButton $dashboard 'Start now'
        } 'WPF Start now during retry window' 8
        $button.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-For { Test-Path -LiteralPath $ready } 'exit-race replacement target' 20 | Out-Null
        $targetPid = [int](Get-Content -LiteralPath $ready -Raw).Split('|')[0]
        if ($targetPid -eq $external.Id) { throw 'Exit-race target identity did not change.' }
        Start-Sleep -Seconds 15
        $events = @(Get-ProfileEvents $root $profile.id)
        $manual = @($events | Where-Object kind -eq 'ExplicitStartDispatched')
        $automatic = @($events | Where-Object kind -eq 'LaunchDispatched')
        $requests = @($events | Where-Object kind -eq 'ExplicitStartRequested')
        if ($manual.Count -ne 1 -or $automatic.Count -ne 0 -or $requests.Count -ne 1 -or
            $requests[0].operationId -ne $manual[0].operationId) {
            throw 'Exit-race UI action produced a missing, duplicate or mismatched launch operation.'
        }
        $after = Read-Configuration $budgetPath
        if ($null -eq $after -or $after.budget.reservedAutomaticAttempts -ne 0) {
            throw 'Exit-race WPF Start now changed the automatic attempt budget.'
        }
        Write-Output 'PASS: callback-driven disappearance enabled WPF Start now; one explicit replacement launched with matching operation IDs, no automatic duplicate and no automatic budget charge.'
    }
    if ($ExerciseAmbiguity) {
        $firstReady = Join-Path $root 'ambiguous-first.ready'
        $secondReady = Join-Path $root 'ambiguous-second.ready'
        $external = Start-Process -FilePath $targetPath -WindowStyle Hidden -PassThru `
            -ArgumentList @('--label', $label, '--ready-file', $firstReady,
                '--exit-after-ms', '180000', '--hidden')
        $external2 = Start-Process -FilePath $targetPath -WindowStyle Hidden -PassThru `
            -ArgumentList @('--label', $label, '--ready-file', $secondReady,
                '--exit-after-ms', '180000', '--hidden')
        Wait-For {
            (Test-Path -LiteralPath $firstReady) -and
                (Test-Path -LiteralPath $secondReady)
        } 'two ambiguous disposable targets' 10 | Out-Null
        $diagnostic = Wait-For {
            $texts = $dashboard.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text))
            foreach ($item in $texts) {
                $name = $item.Current.Name
                if ($name.StartsWith('Cannot verify the target:') -and
                    $name.Contains("PID $($external.Id)") -and
                    $name.Contains("PID $($external2.Id)")) { return $name }
            }
            return $null
        } 'rendered candidate ambiguity' 80
        if (-not $diagnostic.Contains('Automatic actions are suspended.')) {
            throw 'Ambiguity diagnostic did not explain the action suspension.'
        }
        if ((Find-Control $dashboard 'Detection unavailable' `
            ([System.Windows.Automation.ControlType]::Text)) -eq $null) {
            throw 'Dashboard did not show detection unavailable.'
        }
        foreach ($name in @('Start now', 'Stop and pause', 'Restart now')) {
            if (Find-EnabledButton $dashboard $name) {
                throw "$name remained enabled while two targets matched."
            }
        }
        Start-Sleep -Seconds 8
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $budget = Read-Configuration $budgetPath
        if ($null -eq $budget -or $budget.budget.reservedAutomaticAttempts -ne 0) {
            throw 'Ambiguity charged an automatic attempt.'
        }
        if ($external.HasExited -or $external2.HasExited) {
            throw 'Relight terminated an ambiguous disposable target.'
        }
        Write-Output 'PASS: WPF dashboard showed both ambiguous candidate PIDs, suspended unsafe controls, left both targets running and charged no automatic attempt.'
    }

    if ($ExerciseHistoryNavigation) {
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"LaunchDispatched"') -and
                        $line.Contains($profile.id)) { return $true }
                }
            }
            return $false
        } 'durable launch event before History navigation' | Out-Null
        Invoke-Button $dashboard 'View history'
        Wait-For {
            Find-Control $dashboard 'Event history' `
                ([System.Windows.Automation.ControlType]::Text)
        } 'visible History page' | Out-Null
        $profileFilter = Wait-For {
            Find-Control $dashboard 'Filter history by application' `
                ([System.Windows.Automation.ControlType]::ComboBox)
        } 'history application filter'
        Wait-For {
            $selection = $profileFilter.GetCurrentPattern(
                [System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
            if ($selection.Length -eq 1 -and
                $selection[0].Current.Name -eq 'Disposable UI target') { return $true }
            return $false
        } 'selected source profile in History' | Out-Null
        Wait-For {
            $allTexts = $dashboard.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text))
            $hasCount = $false
            $hasDispatch = $false
            foreach ($item in $allTexts) {
                if ($item.Current.Name -match '^Showing [1-9][0-9]* matching event') {
                    $hasCount = $true
                }
                if ($item.Current.Name.StartsWith('Launch Dispatched',
                    [StringComparison]::OrdinalIgnoreCase)) { $hasDispatch = $true }
            }
            return $hasCount -and $hasDispatch
        } 'loaded profile dispatch history' 20 | Out-Null
        $summaryTexts = $dashboard.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))
        $launchSummary = @($summaryTexts | Where-Object {
            $_.Current.Name.StartsWith('Launch Dispatched',
                [StringComparison]::OrdinalIgnoreCase) }) | Select-Object -First 1
        if ($null -eq $launchSummary) { throw 'Retained launch row has no accessible summary.' }
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $eventExpander = $launchSummary
        while ($null -ne $eventExpander -and -not [bool]$eventExpander.GetCurrentPropertyValue(
            [System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty)) {
            $eventExpander = $walker.GetParent($eventExpander)
        }
        if ($null -eq $eventExpander) { throw 'Launch event cannot be expanded through UI Automation.' }
        $eventExpander.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Wait-For {
            $details = $eventExpander.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Edit))
            foreach ($field in $details) {
                $value = $field.GetCurrentPattern(
                    [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
                if ($value.Contains("Profile ID: $($profile.id)") -and
                    $value.Contains('UTC:') -and $value.Contains('Operation ID:')) {
                    return $true
                }
            }
            return $false
        } 'expanded launch correlation details' | Out-Null
        $kindFilter = Find-Control $dashboard 'Filter history by event type' `
            ([System.Windows.Automation.ControlType]::ComboBox)
        if ($null -eq $kindFilter) { throw 'Event type filter is inaccessible.' }
        $kindFilter.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $launchOption = Wait-For {
            Find-Control $kindFilter 'Launch Dispatched' `
                ([System.Windows.Automation.ControlType]::ListItem)
        } 'Launch Dispatched event type option'
        $launchOption.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Invoke-Button $dashboard 'Refresh history'
        Wait-For {
            Find-Control $dashboard 'Showing 1 matching event(s).' `
                ([System.Windows.Automation.ControlType]::Text)
        } 'filtered launch event history' 20 | Out-Null
        Write-Output 'PASS: WPF View history selected the source profile, expanded launch correlation details and filtered to its one launch dispatch.'
    }

    if ($ExplicitAction) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $beforeBudget = Wait-For { Read-Configuration $budgetPath } 'budget before explicit control'
        $selectedPid = $targetPid
        $isRestart = $ExplicitAction -like 'Restart*'
        $actionName = $(if ($isRestart) { 'Restart now' } else { 'Stop and pause' })
        Wait-For {
            $button = Find-Control $dashboard $actionName `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'enabled explicit control after target discovery' 20 | Out-Null
        Invoke-Button $dashboard $actionName
        $confirmation = Find-MessageBox $dashboard $(if ($isRestart) {
            'Restart now?' } else { 'Stop and pause?' })
        Click-NativeMessageChoice $confirmation 'Cancel'
        $stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $stillRunning -or $stillRunning.HasExited) {
            throw 'Canceling explicit control closed the disposable target.'
        }
        $stillRunning.Dispose()
        Start-Sleep -Milliseconds 500
        Wait-For {
            $button = Find-Control $dashboard $actionName `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'explicit control after canceled confirmation' | Out-Null
        Invoke-Button $dashboard $actionName
        $confirmation = Find-MessageBox $dashboard $(if ($isRestart) {
            'Restart now?' } else { 'Stop and pause?' })
        Click-NativeMessageChoice $confirmation 'OK'
        if ($ExplicitAction -like '*Force*') {
            $forceTitle = $(if ($isRestart) { 'Force close before restart?' } else {
                'Force close this application?' })
            $forceChoice = Find-MessageBox $dashboard $forceTitle 25
            Click-NativeMessageChoice $forceChoice $(if ($ExplicitAction -like '*Accept') {
                'Yes' } else { 'No' })
        }
        if ($ExplicitAction -like '*Decline') {
            Wait-For {
                $button = Find-Control $dashboard 'Resume protection' `
                    ([System.Windows.Automation.ControlType]::Button)
                $null -ne $button -and $button.Current.IsEnabled
            } 'paused protection after declined force close' | Out-Null
            $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
            if ($null -eq $running -or $running.HasExited) {
                throw 'Declining force close still stopped the target.'
            }
            $running.Dispose()
        }
        else {
            if ($isRestart) {
                $result = Find-MessageBox $dashboard 'Restart now' 25
                Click-NativeMessageChoice $result 'OK'
                $replacementPid = Wait-For {
                    if (-not (Test-Path -LiteralPath $ready)) { return $null }
                    $candidate = [int](Get-Content -LiteralPath $ready -Raw).Split('|')[0]
                    if ($candidate -ne $targetPid) { return $candidate }
                    return $null
                } 'replacement target after manual restart' 20
                $oldProcess = Get-Process -Id $selectedPid -ErrorAction SilentlyContinue
                if ($null -ne $oldProcess) {
                    try {
                        if (-not $oldProcess.HasExited) {
                            throw 'Manual restart left the originally selected target running.'
                        }
                    }
                    finally { $oldProcess.Dispose() }
                }
                $newProcess = Get-Process -Id $replacementPid -ErrorAction SilentlyContinue
                if ($null -eq $newProcess) { throw 'Manual restart replacement already exited.' }
                try {
                    if ($newProcess.HasExited -or -not [string]::Equals(
                        $newProcess.Path, $targetPath,
                        [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'Manual restart replacement is not the selected disposable target.'
                    }
                }
                finally { $newProcess.Dispose() }
                $targetPid = $replacementPid
            }
            else {
                $result = Find-MessageBox $dashboard 'Stop and pause' 25
                Click-NativeMessageChoice $result 'OK'
                Wait-For {
                    $process = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
                    if ($null -eq $process) { return $true }
                    $exited = $process.HasExited
                    $process.Dispose()
                    return $exited
                } 'selected target exit after explicit stop' 20 | Out-Null
                $targetPid = $null
            }
        }
        $afterBudget = Read-Configuration $budgetPath
        if ($null -eq $afterBudget -or
            $afterBudget.budget.reservedAutomaticAttempts -ne
            $beforeBudget.budget.reservedAutomaticAttempts) {
            throw 'Explicit control changed the automatic recovery budget.'
        }
        Write-Output "PASS: $ExplicitAction honored WPF confirmations, selected-target control, and the unchanged automatic budget."
    }

    if ($ExerciseResetDuplicate) {
        $sourceBudgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $beforeBudget = Wait-For { Read-Configuration $sourceBudgetPath } 'source budget before duplication'
        if ($beforeBudget.budget.reservedAutomaticAttempts -ne 1) {
            throw 'Reset/duplicate acceptance requires one charged automatic attempt.'
        }
        Wait-For {
            $button = Find-Control $dashboard 'Duplicate disabled' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'Duplicate disabled action' | Out-Null
        Invoke-Button $dashboard 'Duplicate disabled'
        $duplicated = Find-MessageBox $dashboard 'Profile duplicated'
        Click-NativeMessageChoice $duplicated 'OK'
        $copy = Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config) { return $null }
            @($config.configuration.profiles | Where-Object id -ne $profile.id) |
                Select-Object -First 1
        } 'disabled duplicated profile'
        if ($copy.id -eq $profile.id -or $copy.enabled -ne $false -or
            ($copy.target | ConvertTo-Json -Depth 10 -Compress) -ne
                ($profile.target | ConvertTo-Json -Depth 10 -Compress) -or
            ($copy.policy | ConvertTo-Json -Depth 10 -Compress) -ne
                ($profile.policy | ConvertTo-Json -Depth 10 -Compress)) {
            throw 'Duplicated profile did not preserve policy/target with a new disabled ID.'
        }
        $copyBudgetPath = Join-Path $root ('Budgets\' + $copy.id.Replace('-', '') + '.json')
        $copyBudget = Wait-For { Read-Configuration $copyBudgetPath } 'new copy budget'
        if ($copyBudget.budget.reservedAutomaticAttempts -ne 0 -or
            (Read-Configuration $sourceBudgetPath).budget.reservedAutomaticAttempts -ne 1) {
            throw 'Disabled copy inherited the source budget or changed its charge.'
        }
        Wait-For {
            Find-EnabledButton $dashboard 'Reset recovery'
        } 'Reset recovery action' | Out-Null
        (Find-EnabledButton $dashboard 'Reset recovery').GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Click-NativeMessageChoice (Find-MessageBox $dashboard 'Reset recovery?') 'Cancel'
        if ((Read-Configuration $sourceBudgetPath).budget.reservedAutomaticAttempts -ne 1) {
            throw 'Canceling recovery reset changed the source budget.'
        }
        Wait-For {
            Find-EnabledButton $dashboard 'Reset recovery'
        } 'Reset recovery after cancel' | Out-Null
        (Find-EnabledButton $dashboard 'Reset recovery').GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Click-NativeMessageChoice (Find-MessageBox $dashboard 'Reset recovery?') 'OK'
        Wait-For {
            $budget = Read-Configuration $sourceBudgetPath
            if ($null -ne $budget -and $budget.budget.reservedAutomaticAttempts -eq 0) {
                return $budget
            }
            return $null
        } 'explicitly reset source budget' | Out-Null
        $copyBudget = Read-Configuration $copyBudgetPath
        if ($null -eq $copyBudget -or $copyBudget.budget.reservedAutomaticAttempts -ne 0) {
            throw 'Resetting the source changed the disabled copy budget.'
        }
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"RecoveryReset"') -and
                        $line.Contains($profile.id)) { return $true }
                }
            }
            return $false
        } 'durable explicit-reset history' | Out-Null
        $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $running -or $running.HasExited) {
            throw 'Duplicating or resetting recovery stopped the source target.'
        }
        $running.Dispose()
        Write-Output 'PASS: WPF duplication created a disabled profile with a new zero-attempt budget; Reset recovery canceled cleanly, then explicitly cleared only the source budget while its target survived.'
    }

    if ($EditSavedExecutable) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $beforeBudget = Wait-For { Read-Configuration $budgetPath } 'budget before edit'
        Invoke-Button $dashboard 'Edit policy'
        $editor = Wait-For {
            Find-Control $dashboard 'Edit protection · Relight' `
                ([System.Windows.Automation.ControlType]::Window)
        } 'profile editor'
        $editedName = 'Disposable UI target edited'
        $nameInput = Find-Control $editor 'Application name' ([System.Windows.Automation.ControlType]::Edit)
        $attemptInput = Find-Control $editor 'Automatic attempt limit' `
            ([System.Windows.Automation.ControlType]::Edit)
        if ($null -eq $nameInput -or $null -eq $attemptInput) {
            throw 'Profile editor does not expose name and attempt limit as accessible fields.'
        }
        $nameInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($editedName)
        $attemptInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('2')
        Wait-For {
            $nodes = $editor.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($node in $nodes) {
                if ($node.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                    $node.Current.Name.Contains('Try up to 2 per episode')) { return $node }
            }
            return $null
        } 'updated policy preview' | Out-Null
        Invoke-Button $editor 'Save changes'
        $edited = Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config) { return $null }
            @($config.configuration.profiles | Where-Object name -eq $editedName) |
                Select-Object -First 1
        } 'saved profile edit' 45
        if ($edited.id -ne $profile.id -or $edited.policy.maximumAutomaticAttempts -ne 2) {
            throw 'The editor changed the profile ID or did not save its attempt limit.'
        }
        $afterBudget = Wait-For { Read-Configuration $budgetPath } 'budget after edit'
        if ($afterBudget.budget.reservedAutomaticAttempts -ne
            $beforeBudget.budget.reservedAutomaticAttempts -or
            $afterBudget.budget.lockedOut -ne $beforeBudget.budget.lockedOut) {
            throw 'The editor changed the recovery budget or lockout.'
        }
        Wait-For {
            Find-Control $dashboard $editedName ([System.Windows.Automation.ControlType]::Text)
        } 'edited dashboard row' | Out-Null
        Write-Output 'PASS: WPF editor saved name and attempt limit with the same profile ID and unchanged recovery budget.'
    }

    if ($TestSavedLaunch) {
        if ($AcceptInitialStart) {
            Wait-For {
                Find-Control $dashboard 'Observing stability' `
                    ([System.Windows.Automation.ControlType]::Text)
            } 'automatic launch observation before Test launch' 30 | Out-Null
        }
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $budgetBeforeTest = Wait-For { Read-Configuration $budgetPath } 'budget before Test launch'
        Invoke-Button $dashboard 'Edit policy'
        $editor = Wait-For {
            Find-Control $dashboard 'Edit protection · Relight' `
                ([System.Windows.Automation.ControlType]::Window)
        } 'profile editor for Test launch'
        foreach ($expectDispatched in @($(if ($AcceptInitialStart) { $false } else { $true }), $false)) {
            Invoke-Button $editor 'Test launch'
            $resultWindow = Find-MessageBox $dashboard 'Test launch result' 75
            $expectedPhrase = if ($expectDispatched) { 'A test launch was dispatched.' }
                else { 'No duplicate launch was dispatched.' }
            $message = Wait-For {
                $nodes = $resultWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)
                foreach ($node in $nodes) {
                    if ($node.Current.Name.Contains($expectedPhrase)) { return $node.Current.Name }
                }
                return $null
            } 'Test launch result detail'
            Click-NativeMessageChoice $resultWindow 'OK'
            if ($expectDispatched) {
                Wait-For { Test-Path -LiteralPath $ready } 'explicit test target start' 15 | Out-Null
            }
        }
        $targetPid = [int](Get-Content -LiteralPath $ready -Raw).Split('|')[0]
        $budgetAfterTest = Wait-For { Read-Configuration $budgetPath } 'budget after Test launch'
        if ($budgetAfterTest.budget.reservedAutomaticAttempts -ne
            $budgetBeforeTest.budget.reservedAutomaticAttempts) {
            throw 'Test launch changed the automatic attempt budget.'
        }
        Wait-For {
            $button = Find-Control $editor 'Test launch' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'Test launch command completion' | Out-Null
        Invoke-Button $editor 'Cancel'
        Wait-For {
            if ($null -eq (Find-Control $dashboard 'Edit protection · Relight' `
                ([System.Windows.Automation.ControlType]::Window))) { return $true }
            return $false
        } 'profile editor close' | Out-Null
        Write-Output 'PASS: WPF Test launch found the disposable target, avoided a duplicate on repeat, and preserved the automatic budget.'
    }

    if ($ExercisePauseResume) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $beforePause = Wait-For { Read-Configuration $budgetPath } 'budget before pause'
        $sessionPath = Wait-For {
            Get-ChildItem -LiteralPath (Join-Path $root 'Sessions') -Recurse `
                -Filter ($profile.id.Replace('-', '') + '.json') -File |
                Select-Object -First 1 -ExpandProperty FullName
        } 'session checkpoint path'
        Wait-For {
            $button = Find-Control $dashboard 'Pause protection' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'Pause protection action' | Out-Null
        Invoke-Button $dashboard 'Pause protection'
        Wait-For {
            $resume = Find-Control $dashboard 'Resume protection' `
                ([System.Windows.Automation.ControlType]::Button)
            $paused = Find-Control $dashboard 'Paused' `
                ([System.Windows.Automation.ControlType]::Text)
            if ($null -ne $resume -and $null -ne $paused -and $resume.Current.IsEnabled) {
                return $resume
            }
            return $null
        } 'paused dashboard state' | Out-Null
        $pausedCheckpoint = Wait-For {
            $saved = Read-Configuration $sessionPath
            if ($null -ne $saved -and $saved.checkpoint.paused -eq $true) { return $saved }
            return $null
        } 'durable pause checkpoint'
        $duringPause = Wait-For { Read-Configuration $budgetPath } 'budget during pause'
        $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $running -or $running.HasExited) { throw 'Pausing protection stopped the target.' }
        $running.Dispose()
        Invoke-Button $dashboard 'Resume protection'
        Wait-For {
            $button = Find-Control $dashboard 'Pause protection' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'resumed dashboard state' | Out-Null
        $resumedCheckpoint = Wait-For {
            $saved = Read-Configuration $sessionPath
            if ($null -ne $saved -and $saved.checkpoint.paused -eq $false) { return $saved }
            return $null
        } 'durable resume checkpoint'
        $afterResume = Wait-For { Read-Configuration $budgetPath } 'budget after resume'
        $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $running -or $running.HasExited) { throw 'Resuming protection stopped the target.' }
        $running.Dispose()
        if ($beforePause.budget.reservedAutomaticAttempts -ne
                $duringPause.budget.reservedAutomaticAttempts -or
            $beforePause.budget.reservedAutomaticAttempts -ne
                $afterResume.budget.reservedAutomaticAttempts) {
            throw 'Pause or resume changed the automatic attempt budget.'
        }
        Write-Output 'PASS: WPF pause and resume kept the disposable target alive and its automatic budget unchanged.'
    }

    if ($ExerciseDisableRemove) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $originalBudget = Wait-For { Read-Configuration $budgetPath } 'budget before disable'
        Invoke-Button $dashboard 'Disable protection'
        Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config) { return $null }
            $saved = @($config.configuration.profiles | Where-Object id -eq $profile.id) |
                Select-Object -First 1
            if ($null -ne $saved -and $saved.enabled -eq $false) { return $saved }
            return $null
        } 'disabled profile configuration' | Out-Null
        Wait-For {
            Find-Control $dashboard 'Enable protection' `
                ([System.Windows.Automation.ControlType]::Button)
        } 'Enable protection action' | Out-Null
        $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $running -or $running.HasExited) { throw 'Disabling protection stopped the target.' }
        $running.Dispose()
        Invoke-Button $dashboard 'Enable protection'
        $enableWarning = Find-MessageBox $dashboard 'Enable automatic start?'
        Click-NativeMessageChoice $enableWarning 'No'
        $declined = Read-Configuration (Join-Path $root 'configuration.json')
        if (@($declined.configuration.profiles | Where-Object id -eq $profile.id |
            Where-Object enabled).Count -ne 0) {
            throw 'Declining the enable warning still enabled protection.'
        }
        Wait-For {
            $button = Find-Control $dashboard 'Enable protection' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'enable action after declining' | Out-Null
        Invoke-Button $dashboard 'Enable protection'
        $enableWarning = Find-MessageBox $dashboard 'Enable automatic start?'
        Click-NativeMessageChoice $enableWarning 'Yes'
        Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config) { return $null }
            $saved = @($config.configuration.profiles | Where-Object id -eq $profile.id) |
                Select-Object -First 1
            if ($null -ne $saved -and $saved.enabled -eq $true) { return $saved }
            return $null
        } 're-enabled profile configuration' | Out-Null
        Wait-For {
            $button = Find-Control $dashboard 'Remove profile' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'Remove profile action' | Out-Null
        Invoke-Button $dashboard 'Remove profile'
        $removeWarning = Find-MessageBox $dashboard 'Remove profile?'
        Click-NativeMessageChoice $removeWarning 'Cancel'
        $retained = Read-Configuration (Join-Path $root 'configuration.json')
        if (@($retained.configuration.profiles | Where-Object id -eq $profile.id).Count -ne 1) {
            throw 'Canceling removal removed the profile.'
        }
        Wait-For {
            $button = Find-Control $dashboard 'Remove profile' `
                ([System.Windows.Automation.ControlType]::Button)
            if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
            return $null
        } 'Remove profile action after cancel' | Out-Null
        Invoke-Button $dashboard 'Remove profile'
        $removeWarning = Find-MessageBox $dashboard 'Remove profile?'
        Click-NativeMessageChoice $removeWarning 'OK'
        Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config) { return $null }
            if (@($config.configuration.profiles | Where-Object id -eq $profile.id).Count -eq 0) {
                return $true
            }
            return $false
        } 'profile removal' | Out-Null
        $finalBudget = Wait-For { Read-Configuration $budgetPath } 'retained budget after removal'
        if ($finalBudget.budget.reservedAutomaticAttempts -ne
            $originalBudget.budget.reservedAutomaticAttempts) {
            throw 'Disable, enable, or removal changed the charged recovery budget.'
        }
        Wait-For {
            foreach ($log in @(Get-ChildItem -LiteralPath (Join-Path $root 'Logs') -File `
                -Filter 'events-*.jsonl')) {
                foreach ($line in @(Get-Content -LiteralPath $log.FullName)) {
                    if ($line.Contains('"kind":"ProfileRemoved"') -and
                        $line.Contains($profile.id)) { return $true }
                }
            }
            return $false
        } 'retained profile-removal history' | Out-Null
        $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $running -or $running.HasExited) { throw 'Removing protection stopped the target.' }
        $running.Dispose()
        Write-Output 'PASS: WPF disable, re-enable and remove preserved the running target and charged budget; canceled confirmations made no change.'
    }

    if ($RegisterSelectedChatGpt) {
        Invoke-Button $dashboard '+ Add application'
        $dialog = Wait-For {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'Add application · Relight')
            $dashboard.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        } 'ChatGPT Add application dialog'
        $choice = Find-Control $dialog 'Installed ChatGPT' ([System.Windows.Automation.ControlType]::RadioButton)
        if ($null -eq $choice) { throw 'Selected ChatGPT choice is unavailable.' }
        $choice.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $identity = Find-Control $dialog `
            'Windows installed app: OpenAI.Codex_2p2nqsd0c76g0!App. Relight identifies the main process by package family and current session; helper processes are not treated as the app.' `
            ([System.Windows.Automation.ControlType]::Text)
        if ($null -eq $identity) { throw 'Selected installed-app identity is not explained.' }
        Invoke-Button $dialog 'Detect now'
        try { $inspection = Wait-For {
            $nodes = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($node in $nodes) {
                if ($node.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
                    ($node.Current.Name.StartsWith('One matching application is running') -or
                     $node.Current.Name.StartsWith('No matching application is running') -or
                     $node.Current.Name.StartsWith('Identity cannot be verified') -or
                     $node.Current.Name.StartsWith('Cannot inspect this application'))) {
                    return $node
                }
            }
            return $null
        } 'selected ChatGPT inspection result' 45 }
        catch {
            $texts = @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } |
                ForEach-Object { $_.Current.Name })
            throw "Selected ChatGPT inspection did not settle. Visible text: $($texts -join ' | ')"
        }
        if ($inspection.Current.Name.StartsWith('Identity cannot be verified') -or
            $inspection.Current.Name.StartsWith('Cannot inspect this application')) {
            throw "Selected ChatGPT inspection is unavailable: $($inspection.Current.Name)"
        }
        Invoke-Button $dialog 'Add and protect'
        $selected = Wait-For {
            $config = Read-Configuration (Join-Path $root 'configuration.json')
            if ($null -eq $config) { return $null }
            @($config.configuration.profiles | Where-Object { $_.target.kind -eq 'PackagedApplication' }) |
                Select-Object -First 1
        } 'saved selected ChatGPT profile' 45
        if ($selected.name -ne 'ChatGPT' -or
            $selected.target.identity -ne 'OpenAI.Codex_2p2nqsd0c76g0!App' -or
            $selected.policy.startAutomaticallyWhenInitiallyAbsent -ne $false) {
            throw 'Saved installed-app profile is not the selected ChatGPT target with initial start off.'
        }
        Write-Output 'PASS: selected ChatGPT was inspected and registered through WPF with the expected package identity and initial automatic start off.'
    }

    if ($ExerciseExit) {
        $budgetPath = Join-Path $root ('Budgets\' + $profile.id.Replace('-', '') + '.json')
        $originalBudget = Wait-For { Read-Configuration $budgetPath } 'budget before exit'
        Invoke-Button $dashboard 'Exit Relight'
        $exitWarning = Find-MessageBox $dashboard 'Exit Relight?'
        Click-NativeMessageChoice $exitWarning 'Cancel'
        $primary.Refresh()
        if ($primary.HasExited) { throw 'Canceling exit closed Relight.' }
        Invoke-Button $dashboard 'Exit Relight'
        $exitWarning = Find-MessageBox $dashboard 'Exit Relight?'
        Click-NativeMessageChoice $exitWarning 'OK'
        Wait-For {
            $primary.Refresh()
            $primary.HasExited
        } 'explicit Relight exit' 20 | Out-Null
        $finalBudget = Read-Configuration $budgetPath
        if ($null -eq $finalBudget -or
            $finalBudget.budget.reservedAutomaticAttempts -ne
            $originalBudget.budget.reservedAutomaticAttempts) {
            throw 'Explicit Relight exit changed the charged recovery budget.'
        }
        $running = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -eq $running -or $running.HasExited) { throw 'Explicit Relight exit stopped the target.' }
        $running.Dispose()
        Write-Output 'PASS: canceling WPF exit kept Relight alive; confirming exit closed Relight while preserving the disposable target and charged budget.'
    }
}
finally {
    if ($null -ne $external2) {
        if (-not $external2.HasExited) {
            Stop-Process -Id $external2.Id
            $external2.WaitForExit(10000) | Out-Null
        }
        $external2.Dispose()
    }
    if ($null -ne $external) {
        if (-not $external.HasExited) {
            Stop-Process -Id $external.Id
            $external.WaitForExit(10000) | Out-Null
        }
        $external.Dispose()
    }
    if ($null -ne $secondary) { $secondary.Dispose() }
    if ($null -ne $primary) {
        if (-not $primary.HasExited) {
            Stop-Process -Id $primary.Id
            $primary.WaitForExit(10000) | Out-Null
        }
        $primary.Dispose()
    }
    if ($null -eq $targetPid -and ($AcceptInitialStart -or $TestSavedLaunch) -and
        $null -ne $ready -and (Test-Path -LiteralPath $ready)) {
        try { $targetPid = [int](Get-Content -LiteralPath $ready -Raw).Split('|')[0] }
        catch { $targetPid = $null }
    }
    if ($null -ne $targetPid) {
        $targetProcess = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($null -ne $targetProcess) {
            try {
                if ([string]::Equals($targetProcess.Path, $targetPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $targetPid
                }
            }
            finally { $targetProcess.Dispose() }
        }
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    if ($resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('relight-setup-ui-', [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($resolvedRoot, $true)
    }
}
