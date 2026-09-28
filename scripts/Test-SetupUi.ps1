param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [string]$Target = (Join-Path $PSScriptRoot '..\tests\Relight.TestTarget\bin\Release\net10.0-windows\Relight.TestTarget.exe'),
    [switch]$RegisterSelectedChatGpt,
    [switch]$VerifyInitialStartConfirmation,
    [switch]$AcceptInitialStart,
    [switch]$EditSavedExecutable,
    [switch]$TestSavedLaunch,
    [switch]$ExercisePauseResume
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

function Read-Configuration([string]$Path) {
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch [IO.IOException] { return $null }
    catch [UnauthorizedAccessException] { return $null }
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
if ($ExercisePauseResume -and -not $AcceptInitialStart) {
    throw 'Pause/resume acceptance requires an automatically started disposable target.'
}

$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$targetPath = (Resolve-Path -LiteralPath $Target).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('relight-setup-ui-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$primary = $null
$secondary = $null
$targetPid = $null
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
    $argumentsInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue(
        (@('--label', $label, '--hidden', '--ready-file', $ready,
            '--exit-after-ms', '180000') -join "`n"))
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
}
finally {
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
