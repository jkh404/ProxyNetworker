$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot '../Test-AndroidClientRecovery.ps1'
$unusedOutput = Join-Path ([IO.Path]::GetTempPath()) "pn-recovery-must-not-exist-$([Guid]::NewGuid().ToString('N'))"
$baseArguments = @{
    AdbPath = 'must-not-be-executed'
    ApkPath = 'must-not-be-read'
    LabToolPath = 'must-not-be-executed'
    LabConfigurationPath = 'must-not-be-read'
    OutputDirectory = $unusedOutput
    AllowControlledTermination = $false
}
$cases = @(
    @{ Name = 'DisabledTermination'; Arguments = @{}; Error = 'Controlled termination must be explicitly enabled.' },
    @{ Name = 'PhysicalDevice'; Arguments = @{ Serial = 'physical-device' }; Error = 'Serial' },
    @{ Name = 'PackageShellData'; Arguments = @{ Package = 'io.github.pn;echo' }; Error = 'Package' },
    @{ Name = 'EscapingEvidencePrefix'; Arguments = @{ EvidencePrefix = '../other' }; Error = 'EvidencePrefix' },
    @{ Name = 'InvalidCycleCount'; Arguments = @{ Cycles = 0 }; Error = 'Cycles' },
    @{
        Name = 'NonEmulatorProperties'
        Arguments = @{
            AllowControlledTermination = $true
            AdbPath = (Join-Path $PSScriptRoot 'AndroidRecoveryFixture.ps1')
        }
        Error = 'Recovery termination requires a verified Android emulator.'
    }
)
foreach ($case in $cases) {
    $arguments = $baseArguments.Clone()
    foreach ($entry in $case.Arguments.GetEnumerator()) { $arguments[$entry.Key] = $entry.Value }
    $rejected = $false
    try { & $helper @arguments | Out-Null }
    catch {
        if (!$_.Exception.Message.Contains($case.Error)) { throw }
        $rejected = $true
    }
    if (!$rejected) { throw "Accepted unsafe recovery parameters: $($case.Name)" }
    if (Test-Path -LiteralPath $unusedOutput) { throw 'A rejected test created an output directory.' }
    Write-Output "PASS $($case.Name)"
}
