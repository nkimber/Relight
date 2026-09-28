param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\Relight.App\bin\Release\net10.0-windows\Relight.exe'),
    [string]$Target = (Join-Path $PSScriptRoot '..\tests\Relight.TestTarget\bin\Release\net10.0-windows\Relight.TestTarget.exe'),
    [switch]$RegisterSelectedChatGpt,
    [switch]$VerifyInitialStartConfirmation
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

$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$targetPath = (Resolve-Path -LiteralPath $Target).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('relight-setup-ui-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$primary = $null
$secondary = $null
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
    if ($VerifyInitialStartConfirmation) {
        $toggle.Toggle()
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
            throw 'Initial automatic start could not be selected.'
        }
        Invoke-Button $dialog 'Add and protect'
        $warning = Wait-For {
            $nested = Find-Control $dashboard 'Enable automatic start?' `
                ([System.Windows.Automation.ControlType]::Window)
            if ($null -ne $nested) { return $nested }
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'Enable automatic start?')
            [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Children, $condition)
        } 'automatic-start confirmation'
        try { $decline = Wait-For {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'No')
            $warning.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        } 'automatic-start No button' }
        catch {
            $all = $warning.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $names = @($all | ForEach-Object {
                "$($_.Current.ControlType.ProgrammaticName):$($_.Current.Name)"
            })
            throw "Warning window PID $($warning.Current.ProcessId), handle $($warning.Current.NativeWindowHandle), nodes $($all.Count): $($names -join ' | ')"
        }
        if ($null -eq $decline) {
            $buttons = @($warning.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button } |
                ForEach-Object { $_.Current.Name })
            throw "The automatic-start warning element $($warning.Current.ControlType.ProgrammaticName) has no accessible No button: $($buttons -join ', ')"
        }
        $className = [Text.StringBuilder]::new(128)
        [RelightUiNative]::GetClassName([IntPtr]::new($decline.Current.NativeWindowHandle),
            $className, $className.Capacity) | Out-Null
        if ($className.ToString() -ne 'Button') {
            throw "The warning's No control has unexpected native class $className."
        }
        [RelightUiNative]::SendMessage([IntPtr]::new($decline.Current.NativeWindowHandle),
            0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
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
    Invoke-Button $dialog 'Add and protect'
    $profile = Wait-For {
        $config = Read-Configuration (Join-Path $root 'configuration.json')
        if ($null -eq $config) { return $null }
        @($config.configuration.profiles | Where-Object name -eq 'Disposable UI target') | Select-Object -First 1
    } 'saved disposable profile' 45
    if ($profile.target.kind -ne 'Executable' -or
        $profile.target.identity -ne $targetPath -or
        $profile.policy.startAutomaticallyWhenInitiallyAbsent -ne $false) {
        throw 'Saved profile does not match the inspected target and selected policy.'
    }
    if ((Find-Control $dashboard 'Disposable UI target' ([System.Windows.Automation.ControlType]::Text)) -eq $null) {
        Wait-For {
            Find-Control $dashboard 'Disposable UI target' ([System.Windows.Automation.ControlType]::Text)
        } 'new dashboard row' | Out-Null
    }
    Write-Output 'PASS: Add dialog was accessible; detection found absence; saved profile kept automatic initial start off; dashboard showed the new profile.'

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
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    if ($resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('relight-setup-ui-', [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($resolvedRoot, $true)
    }
}
