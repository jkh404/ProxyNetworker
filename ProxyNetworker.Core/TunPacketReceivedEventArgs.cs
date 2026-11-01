namespace ProxyNetworker.Core;

public sealed class TunPacketReceivedEventArgs : EventArgs
{
    private readonly ReadOnlyMemory<byte> _packet;
    private readonly byte[]? _packetArray;

    public TunPacketReceivedEventArgs(byte[] packet)
    {
        _packetArray = packet ?? throw new ArgumentNullException(nameof(packet));
        _packet = packet;
    }

    public TunPacketReceivedEventArgs(ReadOnlyMemory<byte> packet)
    {
        if (packet.IsEmpty)
        {
            throw new ArgumentException("Packet cannot be empty.", nameof(packet));
        }

        _packet = packet;
    }

    public byte[] Packet => _packetArray ?? _packet.ToArray();

    public ReadOnlyMemory<byte> PacketMemory => _packet;

    public int Length => _packet.Length;
}
