param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [ValidateSet('Dump', 'Invoke', 'Toggle', 'Close')][string]$Action = 'Dump',
    [string]$Id,
    [string]$Name
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$process = Get-Process -Id $ProcessId -ErrorAction Stop
$handle = $process.MainWindowHandle
if (!$handle) { throw 'The target process has no visible main window.' }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
if ($Action -eq 'Close') {
    $pattern = $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    ([System.Windows.Automation.WindowPattern]$pattern).Close()
    return
}
$conditions = @()
if ($Id) {
    $conditions += [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
}
if ($Name) {
    $conditions += [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
}
$condition = if ($conditions.Count -eq 0) {
    [System.Windows.Automation.Condition]::TrueCondition
} elseif ($conditions.Count -eq 1) {
    $conditions[0]
} else {
    [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.Condition[]]$conditions)
}
$nodes = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
if ($Action -ne 'Dump') {
    if (!$Id -and !$Name) { throw 'A UI selector is required.' }
    if ($nodes.Count -ne 1) { throw "Expected one UI control, found $($nodes.Count)." }
    $node = $nodes[0]
    if (!$node.Current.IsEnabled) { throw 'The target control is disabled.' }
    if ($Action -eq 'Invoke') {
        ([System.Windows.Automation.InvokePattern]$node.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    } else {
        ([System.Windows.Automation.TogglePattern]$node.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    }
    return
}
$nodes | ForEach-Object {
    $current = $_.Current
    if ($current.Name -or $current.AutomationId) {
        $label = if ($current.IsPassword) { '[redacted]' } else {
            [regex]::Replace($current.Name, '(?:vnet_|ptun_)[A-Za-z0-9_-]+', '[redacted]')
        }
        [pscustomobject]@{
            name = $label
            id = $current.AutomationId
            type = $current.ControlType.ProgrammaticName
            enabled = $current.IsEnabled
            offscreen = $current.IsOffscreen
            bounds = $current.BoundingRectangle.ToString()
        }
    }
} | ConvertTo-Json -Compress
