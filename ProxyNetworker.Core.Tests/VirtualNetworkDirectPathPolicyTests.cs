using System.Net;

namespace ProxyNetworker.Core.Tests;

public sealed class VirtualNetworkDirectPathPolicyTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("::1", false)]
    public void PreferenceUsesOnlyPrivateIpv4Ranges(string address, bool expected) =>
        Assert.Equal(expected, VirtualNetworkDirectPathPolicy.IsPrivateAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData(3, false, 4, true, true)]
    [InlineData(3, false, 8, true, true)]
    [InlineData(3, false, 9, true, false)]
    [InlineData(4, true, 3, false, false)]
    [InlineData(20, true, 3, false, true)]
    [InlineData(10, false, 9, false, false)]
    [InlineData(10, false, 8, false, true)]
    [InlineData(100, false, 90, false, false)]
    [InlineData(100, false, 80, false, true)]
    [InlineData(10, true, 9, true, false)]
    [InlineData(10, true, 8, true, true)]
    public void SwitchingHasBoundedPreferenceAndHysteresis(
        double current, bool currentPrivate, double candidate, bool candidatePrivate, bool expected) =>
        Assert.Equal(expected,
            VirtualNetworkDirectPathPolicy.ShouldSwitch(current, currentPrivate, candidate, candidatePrivate));

    [Fact]
    public void InFlightProbesDoNotCountAsLossAndDuplicateAcksDoNotCountTwice()
    {
        var history = new VirtualNetworkPathProbeHistory();
        var now = DateTimeOffset.UtcNow;
        var probe = Probe(now, 1);
        history.RecordSent(probe, now, TimeSpan.FromSeconds(20));

        Assert.Equal(0, history.LossBasisPoints);
        Assert.Equal(1, history.PendingCount);
        Assert.True(history.TryAcknowledge(probe, now.AddMilliseconds(5), TimeSpan.FromSeconds(20)));
        Assert.False(history.TryAcknowledge(probe, now.AddMilliseconds(6), TimeSpan.FromSeconds(20)));
        Assert.Equal(0, history.PendingCount);
        Assert.Equal(0, history.LossBasisPoints);
        Assert.Equal(now.AddMilliseconds(5), history.LastAcknowledgedAt);
    }

    [Fact]
    public void OnlyExpiredProbesContributeToTheCompletedLossWindow()
    {
        var history = new VirtualNetworkPathProbeHistory();
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(20);
        var lost = Probe(now, 1);
        var success = Probe(now.AddSeconds(1), 2);
        history.RecordSent(lost, now, timeout);
        history.RecordSent(success, now.AddSeconds(1), timeout);
        Assert.True(history.TryAcknowledge(success, now.AddSeconds(2), timeout));
        Assert.Equal(0, history.LossBasisPoints);

        history.Expire(now.AddSeconds(21), timeout);

        Assert.Equal(2500, history.LossBasisPoints);
        Assert.Equal(0, history.PendingCount);
        Assert.False(history.TryAcknowledge(lost, now.AddSeconds(21), timeout));
        Assert.Equal(now.AddSeconds(2), history.LastAcknowledgedAt);
    }

    [Fact]
    public void UnknownOrExpiredAcksDoNotRefreshLiveness()
    {
        var history = new VirtualNetworkPathProbeHistory();
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(20);
        var probe = Probe(now, 1);
        history.RecordSent(probe, now, timeout);

        Assert.False(history.TryAcknowledge(Probe(now, 2), now.AddSeconds(1), timeout));
        Assert.False(history.TryAcknowledge(probe, now.AddSeconds(21), timeout));
        Assert.Equal(default, history.LastAcknowledgedAt);
        Assert.Equal(2500, history.LossBasisPoints);
    }

    [Fact]
    public void ProbeHistoryIsBoundedAndSuccessfulWindowsRemainLossless()
    {
        var history = new VirtualNetworkPathProbeHistory();
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromHours(1);
        for (uint index = 0; index < 1000; index++)
        {
            history.RecordSent(Probe(now, index), now, timeout);
        }
        Assert.Equal(VirtualNetworkPathProbeHistory.MaximumPendingProbes, history.PendingCount);

        history = new VirtualNetworkPathProbeHistory();
        for (uint index = 0; index < 1000; index++)
        {
            var probe = Probe(now, index);
            history.RecordSent(probe, now, timeout);
            Assert.True(history.TryAcknowledge(probe, now.AddMilliseconds(1), timeout));
        }
        Assert.Equal(0, history.LossBasisPoints);
        Assert.Equal(0, history.PendingCount);
    }

    [Fact]
    public void FreshSuccessesClearOldFailurePenaltyWithinABoundedNumberOfProbes()
    {
        var history = new VirtualNetworkPathProbeHistory();
        var now = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(20);
        for (uint index = 0; index < 64; index++)
        {
            history.RecordSent(Probe(now, index), now, timeout);
        }
        history.Expire(now.AddSeconds(21), timeout);
        Assert.True(history.LossBasisPoints > 9900);

        for (uint index = 100; index < 106; index++)
        {
            var time = now.AddSeconds(22);
            var probe = Probe(time, index);
            history.RecordSent(probe, time, timeout);
            Assert.True(history.TryAcknowledge(probe, time.AddMilliseconds(1), timeout));
        }

        Assert.InRange(history.LossBasisPoints, 0, 160);
        Assert.Equal(0, history.PendingCount);
    }

    private static VirtualNetworkPathProbe Probe(DateTimeOffset now, uint nonce) =>
        new(IPAddress.Parse("10.66.0.2"), now.ToUnixTimeMilliseconds(), nonce);
}
