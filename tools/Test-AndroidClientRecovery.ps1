param(
    [Parameter(Mandatory = $true)][string]$AdbPath,
    [Parameter(Mandatory = $true)][string]$ApkPath,
    [Parameter(Mandatory = $true)][string]$LabToolPath,
    [Parameter(Mandatory = $true)][string]$LabConfigurationPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][switch]$AllowControlledTermination,
    [ValidatePattern('^emulator-\d+$')][string]$Serial = 'emulator-5556',
    [ValidatePattern('^[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)+$')][string]$Package = 'io.github.jkh404.proxynetworker',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$EvidencePrefix = 'android-session-recovery',
    [ValidateRange(1, 10)][int]$Cycles = 3,
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 90,
    [string]$ExpectedAddress = '198.18.11.4/29',
    [string]$PeerAddress = '198.18.11.2',
    [switch]$RequireNoActivity
)

$ErrorActionPreference = 'Stop'
if (!$AllowControlledTermination) { throw 'Controlled termination must be explicitly enabled.' }
$emulator = (& $AdbPath -s $Serial shell getprop ro.kernel.qemu) -join ''
if ($LASTEXITCODE -ne 0 -or $emulator -ne '1') { throw 'Recovery termination requires a verified Android emulator.' }
$null = [Net.IPAddress]::Parse($ExpectedAddress.Split('/')[0])
$null = [Net.IPAddress]::Parse($PeerAddress)
$apkHash = (Get-FileHash -LiteralPath $ApkPath -Algorithm SHA256).Hash
$installedPath = (& $AdbPath -s $Serial shell pm path $Package) -join ''
if ($LASTEXITCODE -ne 0 -or $installedPath -notmatch '^package:(/data/app/[A-Za-z0-9_./~+=-]+/base\.apk)$') {
    throw 'Expected one installed base APK for the test package.'
}
$installedHash = (& $AdbPath -s $Serial shell sha256sum $Matches[1]) -join ''
if ($LASTEXITCODE -ne 0 -or ($installedHash -split '\s+')[0] -ne $apkHash) {
    throw 'The installed APK does not match the package being tested.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    if (Test-Path -LiteralPath (Join-Path $output "$EvidencePrefix-$cycle.json")) {
        throw 'Use a fresh evidence prefix; previous recovery results are never overwritten.'
    }
}

for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    $result = [ordered]@{
        test = 'android-process-death-recovery'
        cycle = $cycle
        apkSha256 = $apkHash
        passed = $false
        capturedAt = [DateTimeOffset]::UtcNow
    }
    try {
        $before = (& $AdbPath -s $Serial shell pidof $Package) -join ''
        if ($before -notmatch '^\d+$') { throw 'Expected exactly one application process.' }
        $identity = ((& $AdbPath -s $Serial shell cat "/proc/$before/cmdline") -join '').Trim([char]0)
        if ($LASTEXITCODE -ne 0 -or $identity -ne $Package) { throw 'Refusing to terminate an unverified process.' }
        $beforeTun = (& $AdbPath -s $Serial shell ip addr show tun0) -join "`n"
        $beforeIndex = [regex]::Match($beforeTun, '^(\d+): tun0:').Groups[1].Value
        if ($LASTEXITCODE -ne 0 -or !$beforeIndex -or !$beforeTun.Contains($ExpectedAddress)) {
            throw 'The expected baseline TUN is not running.'
        }
        $activities = (& $AdbPath -s $Serial shell dumpsys activity activities) -join "`n"
        $activityPresent = $activities -match "(?m)^\s+\* Hist\s+#\d+: ActivityRecord\{[^\r\n]*$([regex]::Escape($Package))/"
        if ($RequireNoActivity -and $activityPresent) { throw 'The recovery test requires an absent UI Activity.' }
        $result.previousPid = $before
        $result.activityPresentBeforeKill = $activityPresent
        $result.previousTunIndex = $beforeIndex
        & $AdbPath -s $Serial shell su 0 kill -9 $before
        if ($LASTEXITCODE -ne 0) { throw 'Cannot terminate the verified emulator test process.' }

        $watch = [Diagnostics.Stopwatch]::StartNew()
        $after = ''
        $tun = ''
        $afterIndex = ''
        $restored = $false
        do {
            Start-Sleep -Milliseconds 250
            $after = (& $AdbPath -s $Serial shell pidof $Package) -join ''
            if ($after -notmatch '^\d+$' -or $after -eq $before) { continue }
            $tun = (& $AdbPath -s $Serial shell ip addr show tun0 2>&1) -join "`n"
            $afterIndex = [regex]::Match($tun, '^(\d+): tun0:').Groups[1].Value
            $restored = $LASTEXITCODE -eq 0 -and $afterIndex -and $afterIndex -ne $beforeIndex -and $tun.Contains($ExpectedAddress)
        } while (!$restored -and $watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
        $result.recoveredPid = $after
        $result.recoveredTunIndex = $afterIndex
        $result.interfaceRecoverySeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
        $result.tun = $tun
        if (!$restored) { throw 'The session did not restore a new process and TUN before the deadline.' }

        $services = (& $AdbPath -s $Serial shell dumpsys activity services $Package) -join "`n"
        $services | Set-Content -LiteralPath (Join-Path $output "$EvidencePrefix-$cycle-services.txt")
        $pids = @([regex]::Matches($services, 'app=ProcessRecord\{[^}]*?(\d+):[^}/]+/u\d+a\d+\}') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        if ($pids.Count -ne 1 -or $pids[0] -ne $after) { throw 'The restored services are not in the same process.' }
        Start-Sleep -Seconds 1
        $ping = (& $AdbPath -s $Serial shell ping -c 10 -W 2 -M do -s 1372 $PeerAddress) -join "`n"
        $pingCode = $LASTEXITCODE
        $result.fullMtuPing = $ping
        if ($pingCode -ne 0 -or $ping -notmatch '10 received, 0% packet loss') {
            throw 'The restored VPN failed the full-MTU ICMP check.'
        }
        $probe = (& $LabToolPath probe $LabConfigurationPath android) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'The restored port tunnels failed payload verification.' }
        $result.portProbe = $probe | ConvertFrom-Json
        $result.verificationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
        $result.passed = $true
        Write-Output "PASS cycle $cycle`: PID $before -> $after, new TUN in $($result.interfaceRecoverySeconds)s; full MTU and TCP/UDP verified."
    }
    catch {
        $result.error = $_.Exception.Message
        throw
    }
    finally {
        $result.capturedAt = [DateTimeOffset]::UtcNow
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output "$EvidencePrefix-$cycle.json")
    }
}
