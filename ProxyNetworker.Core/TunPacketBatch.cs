namespace ProxyNetworker.Core;

internal sealed class TunPacketBatch
{
    private readonly List<ReadOnlyMemory<byte>> _packets = new(32);

    public int Count => _packets.Count;

    public void Add(ReadOnlyMemory<byte> packet)
    {
        if (!packet.IsEmpty)
        {
            _packets.Add(packet);
        }
    }

    public async ValueTask FlushAsync(ITunDevice tunDevice, CancellationToken cancellationToken = default)
    {
        if (_packets.Count == 0)
        {
            return;
        }

        try
        {
            if (_packets.Count > 1 && tunDevice is ITunBatchDevice batchDevice)
            {
                await batchDevice.SendBatchAsync(_packets, cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var packet in _packets)
            {
                await tunDevice.SendAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _packets.Clear();
        }
    }
}
