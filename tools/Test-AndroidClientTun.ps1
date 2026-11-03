param(
    [Parameter(Mandatory = $true)][string]$AdbPath,
    [ValidatePattern('^emulator-\d+$')][string]$Serial = 'emulator-5556',
    [Parameter(Mandatory = $true)][bool]$ExpectedPresent,
    [string]$TestName = 'android-tun',
    [ValidateRange(1, 30)][int]$TimeoutSeconds = 5
)

$ErrorActionPreference = 'Stop'
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
while ($true) {
    $output = (& $AdbPath -s $Serial shell ip addr show tun0 2>&1 | ForEach-Object { "$_" }) -join "`n"
    $code = $LASTEXITCODE
    if ($code -eq 0 -and $output -match 'tun0:') {
        $present = $true
    }
    elseif ($code -ne 0 -and $output -match 'Device "tun0" does not exist') {
        $present = $false
    }
    else {
        throw "Unable to verify Android TUN state (exit $code): $output"
    }
    if ($present -eq $ExpectedPresent) { break }
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Unexpected Android TUN presence after ${TimeoutSeconds}s: $present." }
    Start-Sleep -Milliseconds 200
}
[pscustomobject]@{
    test = $TestName
    expectedPresent = $ExpectedPresent
    actualPresent = $present
    detail = $output
    passed = $true
    capturedAt = [DateTimeOffset]::UtcNow
} | ConvertTo-Json
