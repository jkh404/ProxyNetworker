$ErrorActionPreference = 'Stop'
$previousMode = $env:PN_UI_DUMP_FIXTURE_MODE
$helper = Join-Path $PSScriptRoot '../Invoke-AndroidClientUi.ps1'
$fixture = Join-Path $PSScriptRoot 'AndroidUiDumpFixture.ps1'
try {
    foreach ($mode in @('IdleFailure', 'MissingFilename', 'InvalidXml')) {
        $env:PN_UI_DUMP_FIXTURE_MODE = $mode
        $rejected = $false
        try { & $helper -AdbPath $fixture -Action Dump | Out-Null }
        catch { $rejected = $true }
        if (!$rejected) { throw "Accepted an invalid UI dump: $mode" }
        Write-Output "PASS $mode"
    }
    $env:PN_UI_DUMP_FIXTURE_MODE = 'Success'
    $result = & $helper -AdbPath $fixture -Action Dump | ConvertFrom-Json
    if ($result.Text -ne 'Fixture') { throw 'A successful fresh UI dump was not returned.' }
    Write-Output 'PASS FreshSuccess'
}
finally {
    $env:PN_UI_DUMP_FIXTURE_MODE = $previousMode
}
