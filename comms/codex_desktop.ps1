param(
    [Parameter(Mandatory)][string]$ChatTitle,
    [string]$ComposerName = '',
    [string]$SendName = 'Send',
    [switch]$Probe
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SpCodexWindow {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

# Exit 2 means the draft and chat were untouched. Exit 3 means manual review.
$edited = $false
try {
    $handle = [SpCodexWindow]::GetForegroundWindow()
    $process = Get-Process | Where-Object { $_.MainWindowHandle -eq $handle } | Select-Object -First 1
    if (!$process -or $process.ProcessName -notin @('ChatGPT', 'Codex') -or
        $process.Path -notlike '*\WindowsApps\OpenAI.Codex_*') { exit 2 }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $editType = [System.Windows.Automation.ControlType]::Edit
    $docType = [System.Windows.Automation.ControlType]::Document
    $buttonType = [System.Windows.Automation.ControlType]::Button
    $selectedPattern = [System.Windows.Automation.SelectionItemPattern]::Pattern
    $headingProperty = [System.Windows.Automation.AutomationProperty]::LookupById(30173)
    $markers = @()
    $composers = @()
    $senders = @()
    foreach ($item in $all) {
        $info = $item.Current
        if ($info.IsOffscreen) { continue }
        $type = $info.ControlType
        $name = $info.Name
        $selected = $null
        $active = $item.TryGetCurrentPattern($selectedPattern, [ref]$selected) -and $selected.Current.IsSelected
        if ($headingProperty) {
            $heading = $item.GetCurrentPropertyValue($headingProperty, $true)
            $active = $active -or ($heading -is [int] -and $heading -eq 80051)
        }
        if ($active) {
            if ($Probe) { Write-Output "Chat marker: $name" }
            if ($name -ceq $ChatTitle) { $markers += $item }
        }
        if ($type -eq $editType -or $type -eq $docType) {
            if ($Probe) { Write-Output "Editor ($($type.ProgrammaticName)): $name" }
            if ($name -ceq $ComposerName -and $type -eq $editType -and $info.IsEnabled) { $composers += $item }
        }
        if ($type -eq $buttonType) {
            if ($Probe -and $name -match 'send|发送|submit|stop|停止') { Write-Output "Button: $name" }
            if ($name -ceq $SendName) { $senders += $item }
        }
    }
    if ($Probe) {
        Write-Output "Matches: chat=$($markers.Count) editor=$($composers.Count) send=$($senders.Count)"
        exit 0
    }
    if ($markers.Count -ne 1 -or $composers.Count -ne 1 -or $senders.Count -ne 1) { exit 2 }
    $composer = $composers[0]
    $sender = $senders[0]
    $textReader = $null
    $valueReader = $null
    if (!$composer.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$textReader) -and
        !$composer.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valueReader)) { exit 2 }
    function Read-Draft {
        if ($textReader) { return $textReader.DocumentRange.GetText(-1) }
        return $valueReader.Current.Value
    }
    function Confirm-Target {
        return ([SpCodexWindow]::GetForegroundWindow() -eq $handle -and
            !$markers[0].Current.IsOffscreen -and $markers[0].Current.Name -ceq $ChatTitle -and
            (!$selectedMarker -or $selectedMarker.Current.IsSelected))
    }
    $selectedMarker = $null
    [void]$markers[0].TryGetCurrentPattern($selectedPattern, [ref]$selectedMarker)
    $invoke = $null
    if (!$sender.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { exit 2 }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8
    $message = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($message) -or ![string]::IsNullOrWhiteSpace((Read-Draft))) { exit 2 }

    $composer.SetFocus()
    if (!(Confirm-Target) -or !$composer.Current.HasKeyboardFocus -or
        ![string]::IsNullOrWhiteSpace((Read-Draft))) { exit 2 }
    $clipboard = [Windows.Forms.Clipboard]::GetDataObject()
    try {
        $edited = $true
        [Windows.Forms.Clipboard]::SetText($message)
        [Windows.Forms.SendKeys]::SendWait('^v')
        for ($i = 0; $i -lt 10; $i++) {
            Start-Sleep -Milliseconds 100
            if ((Read-Draft).Trim() -ceq $message.Trim()) { break }
        }
    } finally {
        if ([Windows.Forms.Clipboard]::GetText() -ceq $message) {
            if ($clipboard) { [Windows.Forms.Clipboard]::SetDataObject($clipboard, $true) }
            else { [Windows.Forms.Clipboard]::Clear() }
        }
    }
    if (!(Confirm-Target) -or !$composer.Current.HasKeyboardFocus -or !$sender.Current.IsEnabled -or
        (Read-Draft).Trim() -cne $message.Trim()) { exit 3 }
    $invoke.Invoke()
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 100
        if (!(Confirm-Target)) { exit 3 }
        if ([string]::IsNullOrWhiteSpace((Read-Draft))) { exit 0 }
    }
    exit 3
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($edited) { exit 3 }
    exit 2
}
