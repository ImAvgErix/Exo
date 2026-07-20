# Exo.ShellDebloat.ps1 - UAC, Windows AI, Explorer declutter, inbox app polish.
# Sourced from Winhance / Chris Titus winutil patterns + Exo competitive defaults.
# Ban-safe for games/AC: no kernel AC hooks, no game file edits.

Set-StrictMode -Version Latest

function Set-ExoRegDword {
    param([string]$Path, [string]$Name, [int]$Value, [ValidateSet('HKCU','HKLM')]$Hive = 'HKCU')
    try {
        $root = if ($Hive -eq 'HKLM') { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
        $rel = $Path -replace '^(HKCU|HKLM):\\','' -replace '^(HKCU|HKLM)\\',''
        $key = $root.CreateSubKey($rel, $true)
        try {
            $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::DWord)
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

function Set-ExoRegString {
    param([string]$Path, [string]$Name, [string]$Value, [ValidateSet('HKCU','HKLM')]$Hive = 'HKCU')
    try {
        $root = if ($Hive -eq 'HKLM') { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
        $rel = $Path -replace '^(HKCU|HKLM):\\','' -replace '^(HKCU|HKLM)\\',''
        $key = $root.CreateSubKey($rel, $true)
        try {
            $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
            return 1
        } finally { $key.Dispose() }
    } catch { return 0 }
}

# --- UAC: no prompts ---
function Set-ExoUacNeverNotify {
    param([switch]$Force)
    $n = 0
    # 0 = elevate without prompting (admin). Full silent admin path.
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'ConsentPromptBehaviorAdmin' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'ConsentPromptBehaviorUser' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'PromptOnSecureDesktop' -Value 0
    # EnableLUA=0 kills UAC completely (reboot). User asked to get rid of prompts.
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'EnableLUA' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'EnableInstallerDetection' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'EnableSecureUIAPaths' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'EnableVirtualization' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'FilterAdministratorToken' -Value 0
    return $n
}

function Test-ExoUacNeverNotify {
    try {
        $a = [int](Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name 'ConsentPromptBehaviorAdmin' -ErrorAction Stop)
        return ($a -eq 0)
    } catch { return $false }
}

# --- Windows AI / Copilot / Recall ---
function Set-ExoWindowsAiGone {
    param([switch]$Force)
    $n = 0
    # Copilot policies
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Policies\Microsoft\Windows\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -Value 1
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -Value 1
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'ShowCopilotButton' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\Shell\Copilot' -Name 'IsCopilotAvailable' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\Shell\Copilot\BingChat' -Name 'IsUserEligible' -Value 0
    # Windows AI / Recall / Click to Do
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\WindowsAI' -Name 'DisableAIDataAnalysis' -Value 1
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\WindowsAI' -Name 'TurnOffSavingSnapshots' -Value 1
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Policies\Microsoft\Windows\WindowsAI' -Name 'DisableAIDataAnalysis' -Value 1
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\WindowsAI' -Name 'AllowRecallEnablement' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer' -Name 'HideRecommendedSection' -Value 1
    # Hide AI components settings page (winutil pattern)
    try {
        $cur = ''
        try { $cur = [string](Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Name 'SettingsPageVisibility' -EA Stop) } catch {}
        if ($cur -notmatch 'aicomponents') {
            $val = if ([string]::IsNullOrWhiteSpace($cur)) { 'hide:aicomponents' } else { ($cur.TrimEnd(';') + ';hide:aicomponents') }
            $n += Set-ExoRegString -Hive HKLM -Path 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' -Name 'SettingsPageVisibility' -Value $val
        }
    } catch { }
    # Input Insights / typing AI
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Input\Settings' -Name 'EnableHwkbTextPrediction' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Input\Settings' -Name 'MultilingualEnabled' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Input\TIPC' -Name 'Enabled' -Value 0
    # Remove AI / Copilot packages
    foreach ($pat in @(
        '*Copilot*', '*Windows.DevHome*', '*OutlookForWindows*', '*Microsoft.YourPhone*',
        '*MicrosoftCorporationII.MicrosoftFamily*', '*Microsoft.BingNews*', '*Microsoft.BingWeather*',
        '*Microsoft.BingSearch*', '*Microsoft.GetHelp*', '*Microsoft.Getstarted*',
        '*Microsoft.MicrosoftOfficeHub*', '*Microsoft.Todos*', '*Microsoft.PowerAutomateDesktop*',
        '*Clipchamp*', '*Microsoft.Windows.Ai*', '*MicrosoftWindows.Client.WebExperience*',
        '*Microsoft.Edge.GameAssist*', '*Microsoft.WindowsFeedbackHub*'
    )) {
        try {
            Get-AppxPackage -AllUsers $pat -ErrorAction SilentlyContinue | ForEach-Object {
                try {
                    Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue
                    $n++
                } catch {
                    try { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue; $n++ } catch { }
                }
            }
            Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like ($pat -replace '\*','') -or $_.DisplayName -match ($pat.Trim('*') -replace '\.','\.') } |
                ForEach-Object {
                    try {
                        Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null
                        $n++
                    } catch { }
                }
        } catch { }
    }
    # Disable Copilot / AI related tasks
    try {
        Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
            $_.TaskName -match '(?i)Copilot|Recall|WindowsAI|FamilySafety|UsageData|Customer Experience' -or
            $_.TaskPath -match '(?i)WindowsAI|Copilot'
        } | ForEach-Object {
            try { Disable-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -EA SilentlyContinue | Out-Null; $n++ } catch { }
        }
    } catch { }
    return $n
}

function Test-ExoWindowsAiGone {
    try {
        $v = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -ErrorAction Stop)
        return ($v -eq 1)
    } catch {
        try {
            $v2 = [int](Get-ItemPropertyValue -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -ErrorAction Stop)
            return ($v2 -eq 1)
        } catch { return $false }
    }
}

# --- Explorer declutter (Winhance / CTT style) ---
function Set-ExoExplorerDeclutter {
    param([switch]$Force)
    $n = 0
    $adv = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
    # File Explorer opens to This PC
    $n += Set-ExoRegDword -Path $adv -Name 'LaunchTo' -Value 1
    # Show file extensions / hidden (power user)
    $n += Set-ExoRegDword -Path $adv -Name 'HideFileExt' -Value 0
    $n += Set-ExoRegDword -Path $adv -Name 'Hidden' -Value 1
    # Taskbar: no Task View, no Widgets, no Chat, search icon only / hide
    $n += Set-ExoRegDword -Path $adv -Name 'ShowTaskViewButton' -Value 0
    $n += Set-ExoRegDword -Path $adv -Name 'TaskbarDa' -Value 0          # widgets
    $n += Set-ExoRegDword -Path $adv -Name 'TaskbarMn' -Value 0          # chat
    $n += Set-ExoRegDword -Path $adv -Name 'ShowCopilotButton' -Value 0
    $n += Set-ExoRegDword -Path $adv -Name 'TaskbarAl' -Value 0          # left align (optional classic)
    # No recent/frequent in Quick access noise
    $n += Set-ExoRegDword -Path $adv -Name 'Start_TrackDocs' -Value 0
    $n += Set-ExoRegDword -Path $adv -Name 'Start_TrackProgs' -Value 0
    # Hide People / Meet
    $n += Set-ExoRegDword -Path $adv -Name 'PeopleBand' -Value 0
    # Compact view Win11
    $n += Set-ExoRegDword -Path $adv -Name 'UseCompactMode' -Value 1
    # Snap Assist flyouts less noisy
    $n += Set-ExoRegDword -Path $adv -Name 'EnableSnapBar' -Value 0
    $n += Set-ExoRegDword -Path $adv -Name 'EnableSnapAssistFlyout' -Value 0
    # Search box -> icon / hide (2=icon, 0=hide on some builds)
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Search' -Name 'SearchboxTaskbarMode' -Value 0

    # Recycle Bin: remove from DESKTOP, keep in File Explorer navigation
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel' -Name '{645FF040-5081-101B-9F08-00AA002F954E}' -Value 1
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu' -Name '{645FF040-5081-101B-9F08-00AA002F954E}' -Value 1
    # Show Recycle Bin under This PC / navigation pane (NameSpace)
    try {
        $ns = 'Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{645FF040-5081-101B-9F08-00AA002F954E}'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($ns, $true)
        if ($key) { $key.Dispose(); $n++ }
    } catch { }
    # Hide OneDrive from navigation when possible
    try {
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}' -Name 'System.IsPinnedToNameSpaceTree' -Value 0
    } catch { }

    # Home / Gallery less junk (Win11)
    $n += Set-ExoRegDword -Path $adv -Name 'ShowFrequent' -Value 0
    $n += Set-ExoRegDword -Path $adv -Name 'ShowRecent' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer' -Name 'ShowRecent' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer' -Name 'ShowFrequent' -Value 0

    # Notifications quieter
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\PushNotifications' -Name 'ToastEnabled' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Policies\Microsoft\Windows\Explorer' -Name 'DisableNotificationCenter' -Value 1

    # Restart explorer soft so icons apply (best-effort)
    try {
        $shell = Get-Process explorer -ErrorAction SilentlyContinue
        if ($shell) {
            Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 400
            Start-Process explorer.exe | Out-Null
        }
    } catch { }
    return $n
}

function Test-ExoExplorerDeclutter {
    try {
        $launch = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'LaunchTo' -ErrorAction Stop)
        $bin = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel' -Name '{645FF040-5081-101B-9F08-00AA002F954E}' -ErrorAction Stop)
        return ($launch -eq 1 -and $bin -eq 1)
    } catch { return $false }
}

# --- Inbox apps: Notepad, Photos, Snipping Tool ---
function Set-ExoInboxAppsOptimized {
    param([switch]$Force)
    $n = 0
    # Notepad: open new window, no restart session restore spam
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Notepad' -Name 'StatusBar' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer\DontShowMeThisDialogAgain' -Name 'Microsoft.WindowsNotepad_8wekyb3d8bbwe' -Value 1
    try {
        # Store Notepad preferences when present
        $np = 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\Microsoft.WindowsNotepad_8wekyb3d8bbwe\Schemas'
        # Soft: disable "open previous files" via packaging state if key exists
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'SubscribedContent-338389Enabled' -Value 0
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'SubscribedContent-310093Enabled' -Value 0
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'SystemPaneSuggestionsEnabled' -Value 0
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'SoftLandingEnabled' -Value 0
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'SilentInstalledAppsEnabled' -Value 0
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'PreInstalledAppsEnabled' -Value 0
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager' -Name 'OemPreInstalledAppsEnabled' -Value 0
    } catch { }

    # Photos: prevent background startup / tips
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\Microsoft.Windows.Photos_8wekyb3d8bbwe\PersistedStorageItemTableSet' -Name 'DisableStartup' -Value 1
    try {
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\Microsoft.Windows.Photos_8wekyb3d8bbwe' -Name 'Disabled' -Value 1
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\Microsoft.Windows.Photos_8wekyb3d8bbwe' -Name 'DisabledByUser' -Value 1
    } catch { }

    # Snipping Tool / ScreenSketch: less first-run / tips
    try {
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\Microsoft.ScreenSketch_8wekyb3d8bbwe' -Name 'Disabled' -Value 1
        $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\Microsoft.ScreenSketch_8wekyb3d8bbwe' -Name 'DisabledByUser' -Value 1
    } catch { }
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name 'ScreenshotKeyEnabled' -Value 1

    # Calculator / Clock background access off
    foreach ($pkg in @(
        'Microsoft.WindowsCalculator_8wekyb3d8bbwe',
        'Microsoft.WindowsAlarms_8wekyb3d8bbwe',
        'Microsoft.WindowsMaps_8wekyb3d8bbwe',
        'Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe',
        'Microsoft.MicrosoftStickyNotes_8wekyb3d8bbwe',
        'Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe',
        'Microsoft.YourPhone_8wekyb3d8bbwe',
        'Microsoft.GetHelp_8wekyb3d8bbwe'
    )) {
        try {
            $n += Set-ExoRegDword -Hive HKCU -Path "Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\$pkg" -Name 'Disabled' -Value 1
            $n += Set-ExoRegDword -Hive HKCU -Path "Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\$pkg" -Name 'DisabledByUser' -Value 1
        } catch { }
    }

    # Search / web suggestions off (Explorer + search)
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Search' -Name 'BingSearchEnabled' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Search' -Name 'CortanaConsent' -Value 0
    $n += Set-ExoRegDword -Hive HKCU -Path 'Software\Microsoft\Windows\CurrentVersion\Search' -Name 'AllowSearchToUseLocation' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\Windows Search' -Name 'AllowCortana' -Value 0
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\Windows Search' -Name 'DisableWebSearch' -Value 1
    $n += Set-ExoRegDword -Hive HKLM -Path 'SOFTWARE\Policies\Microsoft\Windows\Windows Search' -Name 'ConnectedSearchUseWeb' -Value 0

    return $n
}

function Test-ExoInboxAppsOptimized {
    try {
        $bing = [int](Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name 'BingSearchEnabled' -ErrorAction Stop)
        return ($bing -eq 0)
    } catch { return $false }
}

function Invoke-ExoShellDebloatPack {
    param([switch]$Force)
    return [pscustomobject]@{
        uac      = Set-ExoUacNeverNotify -Force:$Force
        windowsAi = Set-ExoWindowsAiGone -Force:$Force
        explorer = Set-ExoExplorerDeclutter -Force:$Force
        inbox    = Set-ExoInboxAppsOptimized -Force:$Force
    }
}

function Get-ExoShellDebloatFeatureRows {
    @(
        [ordered]@{
            title = 'No UAC prompts'
            detail = 'Admin elevation without secure-desktop prompts (EnableLUA off after reboot).'
            active = [bool](Test-ExoUacNeverNotify)
        },
        [ordered]@{
            title = 'Windows AI removed'
            detail = 'Copilot / Recall / AI components policies + package purge (CTT/Winhance-class).'
            active = [bool](Test-ExoWindowsAiGone)
        },
        [ordered]@{
            title = 'Explorer decluttered'
            detail = 'This PC default, recycle bin off desktop (stays in Explorer), no widgets/chat/Task View clutter.'
            active = [bool](Test-ExoExplorerDeclutter)
        },
        [ordered]@{
            title = 'Inbox apps quiet'
            detail = 'Photos/Snipping/Notepad-related background access off; Bing/Cortana web search off.'
            active = [bool](Test-ExoInboxAppsOptimized)
        }
    )
}
