using System.Collections;
using System.Net;
using System.Reflection;

namespace ProxyNetworker.Core.Tests;

public sealed class VirtualNetworkPathSelectionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly IPAddress Peer = IPAddress.Parse("10.66.0.2");
    private static readonly IPEndPoint Public = new(IPAddress.Parse("203.0.113.2"), 13322);
    private static readonly IPEndPoint Private = new(IPAddress.Parse("10.9.8.7"), 13323);
    private static readonly IPEndPoint Relay = new(IPAddress.Parse("203.0.113.1"), 13324);

    [Fact]
    public void DefaultPrefersProvenPrivateCandidateWithoutAnAddressAllowlist()
    {
        var options = Options();
        Assert.True(options.EnableStableDirectPathSelection);
        Assert.Empty(options.AllowedDirectCandidateAddresses);
        using var tunnel = Create(options);
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 2);

        Assert.Equal(Private, Choose(tunnel));
    }

    [Fact]
    public void ClearlyBetterPublicCandidateIsNotBlockedByPrivatePreference()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 30);

        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void NewPrivateProofReplacesPublicStartupFallbackAndInvalidatesCache()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Assert.Equal(Public, Choose(tunnel));
        Assert.NotNull(Field(tunnel, "_cachedForwardingDecision"));

        Acknowledge(tunnel, Private);

        Assert.Null(Field(tunnel, "_cachedForwardingDecision"));
        Assert.Equal(Private, Choose(tunnel));
    }

    [Fact]
    public void SmallPublicScoreFluctuationsDoNotMoveThePrivatePath()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 2);
        Measure(tunnel, Private, 2);
        Assert.Equal(Private, Choose(tunnel));

        for (var index = 0; index < 20; index++)
        {
            Property(Path(tunnel, Public), "EwmaRttMs", index % 2 == 0 ? 0d : 1d);
            Assert.Equal(Private, Choose(tunnel));
        }
    }

    [Fact]
    public void DisablingPolicyRestoresRawScoreRanking()
    {
        var options = Options();
        options.EnableStableDirectPathSelection = false;
        using var tunnel = Create(options);
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 2);

        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void OutstandingProbeDoesNotFalselyDowngradeHealthyPrivatePath()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));
        var history = History(Path(tunnel, Private));
        var now = DateTimeOffset.UtcNow;
        history.RecordSent(new VirtualNetworkPathProbe(Peer, now.ToUnixTimeMilliseconds(), 77),
            now, TimeSpan.FromSeconds(20));

        Assert.Equal(0, history.LossBasisPoints);
        Assert.Equal(Private, Choose(tunnel));
    }

    [Fact]
    public void OutboundTrafficCannotKeepAFailedPathAlive()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        Measure(tunnel, Private, 1, old);

        Invoke(tunnel, "RecordDirectTraffic", Peer, Private, 1400L, 0L);

        Assert.Equal(old, Path(tunnel, Private).GetType().GetProperty("LastSuccess")!.GetValue(Path(tunnel, Private)));
        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void IncomingTrafficDoesNotReplaceFreshBidirectionalProbeProof()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        Invoke(tunnel, "RecordDirectTraffic", Peer, Private, 0L, 1400L);

        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void AllExpiredDirectProofsFallBackToConfiguredRelayAndRecoveryIsAutomatic()
    {
        using var tunnel = Create();
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        Measure(tunnel, Public, 1, old);
        Measure(tunnel, Private, 1, old);
        Assert.Equal(Relay, Choose(tunnel));

        Acknowledge(tunnel, Private);

        Assert.Equal(Private, Choose(tunnel));
    }

    [Fact]
    public void UnsolicitedProbeAckCannotCreateAUsablePrivatePath()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Invoke(tunnel, "RecordPathMeasurement", Peer, Private,
            new VirtualNetworkPathProbe(Peer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 55));

        Assert.Null(Path(tunnel, Private).GetType().GetProperty("EwmaRttMs")!.GetValue(Path(tunnel, Private)));
        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void ExplicitFailureCooldownStillForcesRelay()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));

        Invoke(tunnel, "SuspendDirectPaths", Peer, "test failure");

        Assert.Equal(Relay, Choose(tunnel));
    }

    [Fact]
    public void ActualProbeLossPenaltyOverridesPrivatePreference()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));
        var history = History(Path(tunnel, Private));
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        history.RecordSent(new VirtualNetworkPathProbe(Peer, old.ToUnixTimeMilliseconds(), 99),
            old, TimeSpan.FromSeconds(20));
        history.Expire(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20));

        Assert.True(history.LossBasisPoints > 0);
        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void FreshPrivateProofsRecoverThePreferredPathAfterRealProbeLoss()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));
        var history = History(Path(tunnel, Private));
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        history.RecordSent(new VirtualNetworkPathProbe(Peer, old.ToUnixTimeMilliseconds(), 99),
            old, TimeSpan.FromSeconds(20));
        history.Expire(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(20));
        Assert.Equal(Public, Choose(tunnel));

        for (var index = 0; index < 6; index++)
        {
            Acknowledge(tunnel, Private);
        }

        Assert.Equal(Private, Choose(tunnel));
    }

    [Fact]
    public void ExpiredPrivateProofInvalidatesCacheEvenWhenPublicHasTheSameMeshCost()
    {
        using var tunnel = Create();
        Measure(tunnel, Public, 1);
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));
        Measure(tunnel, Private, 1, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        typeof(VirtualNetworkTunnel).GetField("_nextPeerCleanupTick", PrivateInstance)!.SetValue(tunnel, 0L);

        Invoke(tunnel, "CleanupExpiredPeers");

        Assert.Null(Field(tunnel, "_cachedForwardingDecision"));
        Assert.Equal(Public, Choose(tunnel));
    }

    [Fact]
    public void ForwardingLogsIncludeEndpointChangesOnTheSameMeshRoute()
    {
        var logs = new List<string>();
        using var tunnel = Create(logger: entry => logs.Add(entry.Message));
        Measure(tunnel, Public, 1);
        Assert.Equal(Public, Choose(tunnel));
        Measure(tunnel, Private, 1);
        Assert.Equal(Private, Choose(tunnel));

        var selections = logs.Where(line => line.Contains("direct path selected", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, selections.Length);
        Assert.Contains(Public.ToString(), selections[0]);
        Assert.Contains(Private.ToString(), selections[1]);
    }

    private static VirtualNetworkTunnelOptions Options() => new()
    {
        AuthenticationKey = new byte[32],
        NodeAddress = IPAddress.Parse("10.66.0.1"),
        RemoteEndPoint = Relay,
        IncludeLocalInterfaceCandidates = false
    };

    private static VirtualNetworkTunnel Create(
        VirtualNetworkTunnelOptions? options = null, Action<ProxyNetworkerLogEntry>? logger = null)
    {
        var tunnel = new VirtualNetworkTunnel(new NullTunDevice(), options ?? Options(), logger);
        Invoke(tunnel, "UpdateDirectCandidates", Peer, new[] { Public, Private });
        return tunnel;
    }

    private static void Measure(VirtualNetworkTunnel tunnel, IPEndPoint endpoint, double rtt, DateTimeOffset? at = null)
    {
        var path = Path(tunnel, endpoint);
        var now = at ?? DateTimeOffset.UtcNow;
        var probe = new VirtualNetworkPathProbe(Peer, now.ToUnixTimeMilliseconds(), 1);
        History(path).RecordSent(probe, now, TimeSpan.FromSeconds(20));
        Assert.True(History(path).TryAcknowledge(probe, now, TimeSpan.FromSeconds(20)));
        Property(path, "EwmaRttMs", rtt);
        Property(path, "JitterMilliseconds", 0d);
        Property(path, "LastSuccess", now);
    }

    private static void Acknowledge(VirtualNetworkTunnel tunnel, IPEndPoint endpoint)
    {
        var now = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        var probe = new VirtualNetworkPathProbe(Peer, now.ToUnixTimeMilliseconds(), 2);
        History(Path(tunnel, endpoint)).RecordSent(probe, now, TimeSpan.FromSeconds(20));
        Invoke(tunnel, "RecordPathMeasurement", Peer, endpoint, probe);
    }

    private static IPEndPoint Choose(VirtualNetworkTunnel tunnel)
    {
        Invoke(tunnel, "RefreshMeshRoutes");
        typeof(VirtualNetworkTunnel).GetField("_cachedForwardingDecisionAt", PrivateInstance)!
            .SetValue(tunnel, default(DateTimeOffset));
        var decision = Invoke(tunnel, "SelectForwardingDecision", Peer)!;
        return (IPEndPoint)decision.GetType().GetProperty("EndPoint")!.GetValue(decision)!;
    }

    private static object Path(VirtualNetworkTunnel tunnel, IPEndPoint endpoint)
    {
        var paths = (IDictionary)State(tunnel).GetType().GetProperty("Paths")!.GetValue(State(tunnel))!;
        return paths[endpoint.ToString()]!;
    }

    private static object State(VirtualNetworkTunnel tunnel) =>
        ((IDictionary)Field(tunnel, "_directPeersByAddress")!)[Peer.ToString()]!;

    private static VirtualNetworkPathProbeHistory History(object path) =>
        (VirtualNetworkPathProbeHistory)path.GetType().GetProperty("ProbeHistory")!.GetValue(path)!;

    private static void Property(object target, string name, object value) =>
        target.GetType().GetProperty(name)!.SetValue(target, value);

    private static object? Invoke(VirtualNetworkTunnel tunnel, string name, params object?[] values) =>
        typeof(VirtualNetworkTunnel).GetMethod(name, PrivateInstance)!.Invoke(tunnel, values);

    private static object? Field(VirtualNetworkTunnel tunnel, string name) =>
        typeof(VirtualNetworkTunnel).GetField(name, PrivateInstance)!.GetValue(tunnel);

    private sealed class NullTunDevice : ITunDevice
    {
        public string Name => "path-selection-test";
        public bool IsStarted => false;
        public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived
        {
            add { }
            remove { }
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
