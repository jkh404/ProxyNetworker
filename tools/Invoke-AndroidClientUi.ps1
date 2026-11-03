param(
    [Parameter(Mandatory = $true)][string]$AdbPath,
    [ValidatePattern('^emulator-\d+$')][string]$Serial = 'emulator-5556',
    [ValidateSet('Dump', 'Tap')][string]$Action = 'Dump',
    [string]$Text,
    [string]$Id,
    [string]$Class,
    [int]$Index = 0
)

$ErrorActionPreference = 'Stop'
$remoteFile = "/data/local/tmp/pn-client-ui-$([Guid]::NewGuid().ToString('N')).xml"
try {
    $dump = (& $AdbPath -s $Serial shell uiautomator dump $remoteFile 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $dump -match '(?i)\bERROR\b' -or
        $dump -notmatch 'dumped to:' -or !$dump.Contains($remoteFile)) {
        throw "Android UI dump failed: $dump"
    }
    $xml = (& $AdbPath -s $Serial shell cat $remoteFile) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the fresh Android UI dump.' }
    [xml]$ui = $xml
    if ($ui.DocumentElement.Name -ne 'hierarchy') { throw 'Invalid Android UI hierarchy.' }
}
finally {
    & $AdbPath -s $Serial shell rm -f $remoteFile | Out-Null
}
$nodes = @($ui.SelectNodes('//node') | Where-Object {
    (!$Text -or $_.GetAttribute('text') -eq $Text) -and
    (!$Id -or $_.GetAttribute('resource-id') -eq $Id) -and
    (!$Class -or $_.GetAttribute('class') -eq $Class)
})
if ($Action -eq 'Tap') {
    if (!$Text -and !$Id -and !$Class) { throw 'A UI selector is required.' }
    if ($Index -lt 0 -or $Index -ge $nodes.Count) { throw "No matching Android UI element at index $Index." }
    $node = $nodes[$Index]
    if ($node.GetAttribute('enabled') -eq 'false') { throw 'The Android control is disabled.' }
    $match = [regex]::Match($node.GetAttribute('bounds'), '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$')
    if (!$match.Success) { throw 'The Android control has invalid bounds.' }
    $x = [int](([int]$match.Groups[1].Value + [int]$match.Groups[3].Value) / 2)
    $y = [int](([int]$match.Groups[2].Value + [int]$match.Groups[4].Value) / 2)
    & $AdbPath -s $Serial shell input tap $x $y
    if ($LASTEXITCODE -ne 0) { throw 'Android tap failed.' }
    Write-Output "Tapped $($node.GetAttribute('class')) at $x,$y."
    return
}
$nodes | Where-Object {
    $_.GetAttribute('text') -or $_.GetAttribute('content-desc')
} | ForEach-Object {
    $label = $_.GetAttribute('text')
    if ($_.GetAttribute('password') -eq 'true') { $label = '[redacted]' }
    $label = [regex]::Replace($label, '(?:vnet_|ptun_)[A-Za-z0-9_-]+', '[redacted]')
    [pscustomobject]@{
        Text = $label
        Name = $_.GetAttribute('content-desc')
        Id = $_.GetAttribute('resource-id')
        Class = $_.GetAttribute('class')
        Package = $_.GetAttribute('package')
        Bounds = $_.GetAttribute('bounds')
    }
} | ConvertTo-Json -Compress
