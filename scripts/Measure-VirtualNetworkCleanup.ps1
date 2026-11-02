param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateRange(1, 1000000)]
    [int]$Iterations = 10000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$core = Join-Path $root "ProxyNetworker.Core/bin/$Configuration/net10.0/ProxyNetworker.Core.dll"
$tests = Join-Path $root "ProxyNetworker.Core.Tests/bin/$Configuration/net10.0/ProxyNetworker.Core.Tests.dll"
if (-not (Test-Path -LiteralPath $core) -or -not (Test-Path -LiteralPath $tests)) {
    throw 'Build the Release core tests before running this probe.'
}

if (-not ('ProxyNetworkerCleanupProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;

public static class ProxyNetworkerCleanupProbe
{
    public static string Run(string corePath, string testsPath, int iterations)
    {
        var core = Assembly.LoadFrom(corePath);
        var tests = Assembly.LoadFrom(testsPath);
        var optionsType = core.GetType("ProxyNetworker.Core.VirtualNetworkTunnelOptions", true);
        var tunnelType = core.GetType("ProxyNetworker.Core.VirtualNetworkTunnel", true);
        var probeType = core.GetType("ProxyNetworker.Core.VirtualNetworkPathProbe", true);
        var tunType = tests.GetType("ProxyNetworker.Core.Tests.MeshUdpIntegrationTests+TestTunDevice", true);
        var options = Activator.CreateInstance(optionsType);
        optionsType.GetProperty("AuthenticationKey").SetValue(options, new byte[32]);
        var tun = Activator.CreateInstance(tunType, true);
        var tunnel = Activator.CreateInstance(tunnelType, new object[] { tun, options, null });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var update = tunnelType.GetMethod("UpdateDirectCandidates", flags);
            var record = tunnelType.GetMethod("RecordPathMeasurement", flags);
            for (var index = 2; index <= 4; index++)
            {
                var address = IPAddress.Parse("10.66.0." + index);
                var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2." + index), 13000 + index);
                update.Invoke(tunnel, new object[] { address, new[] { endpoint } });
                var probe = Activator.CreateInstance(probeType, new object[] {
                    address, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1, (uint)index });
                record.Invoke(tunnel, new object[] { address, endpoint, probe });
            }

            var cleanup = (Action)tunnelType.GetMethod("CleanupExpiredPeers", flags)
                .CreateDelegate(typeof(Action), tunnel);
            var routes = tunnelType.GetField("_meshRoutes", flags);
            var originalRoutes = routes.GetValue(tunnel);
            var throttled = Measure(cleanup, iterations);
            var snapshotReplaced = !ReferenceEquals(originalRoutes, routes.GetValue(tunnel));
            var gate = tunnelType.GetField("_nextPeerCleanupTick", flags);
            var forced = "not available in this build";
            if (gate != null)
            {
                object zeroTick = 0L;
                Action sweep = () => { gate.SetValue(tunnel, zeroTick); cleanup(); };
                forced = Measure(sweep, iterations);
            }

            return $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
                $"Fixture: 4 nodes, {iterations} calls, no network I/O\n" +
                $"Normal cleanup: {throttled}; route snapshot replaced={snapshotReplaced}\n" +
                $"Forced maintenance sweeps: {forced}";
        }
        finally { ((IDisposable)tunnel).Dispose(); }
    }

    private static string Measure(Action operation, int iterations)
    {
        for (var index = 0; index < 128; index++) operation();
        GC.Collect();
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++) operation();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        return $"{elapsed.TotalMilliseconds:F2} ms, {allocated / (double)iterations:F0} allocated bytes/call";
    }
}
'@
}

[ProxyNetworkerCleanupProbe]::Run($core, $tests, $Iterations)
