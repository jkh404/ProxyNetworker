namespace ProxyNetworker.Core;

internal sealed class VirtualNetworkPathProbeHistory
{
    internal const int MaximumPendingProbes = 64;

    private readonly Dictionary<(long Timestamp, uint Nonce), DateTimeOffset> _pending = new();
    private double _lossBasisPoints;

    internal int PendingCount => _pending.Count;

    internal DateTimeOffset LastAcknowledgedAt { get; private set; }

    internal int LossBasisPoints =>
        Math.Clamp((int)Math.Round(_lossBasisPoints, MidpointRounding.AwayFromZero), 0, 10_000);

    internal void RecordSent(VirtualNetworkPathProbe probe, DateTimeOffset now, TimeSpan timeout)
    {
        Expire(now, timeout);
        var key = (probe.TimestampMilliseconds, probe.Nonce);
        if (_pending.ContainsKey(key))
        {
            return;
        }

        if (_pending.Count >= MaximumPendingProbes)
        {
            var oldest = _pending.MinBy(static item => item.Value).Key;
            _pending.Remove(oldest);
            Complete(succeeded: false);
        }
        _pending.Add(key, now);
    }

    internal bool TryAcknowledge(VirtualNetworkPathProbe probe, DateTimeOffset now, TimeSpan timeout)
    {
        var key = (probe.TimestampMilliseconds, probe.Nonce);
        if (!_pending.Remove(key, out var sentAt))
        {
            return false;
        }

        if (now < sentAt || now - sentAt > timeout)
        {
            Complete(succeeded: false);
            return false;
        }

        Complete(succeeded: true);
        LastAcknowledgedAt = now;
        return true;
    }

    internal void Expire(DateTimeOffset now, TimeSpan timeout)
    {
        foreach (var key in _pending
            .Where(item => now - item.Value > timeout)
            .Select(static item => item.Key)
            .ToArray())
        {
            _pending.Remove(key);
            Complete(succeeded: false);
        }
    }

    private void Complete(bool succeeded)
    {
        // Recent completed probes govern recovery; in-flight probes never affect this estimate.
        _lossBasisPoints = succeeded
            ? _lossBasisPoints * 0.5
            : _lossBasisPoints + (10_000 - _lossBasisPoints) * 0.25;
    }
}
