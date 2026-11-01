using System.Buffers.Binary;

namespace ProxyNetworker.Core;

internal static class VirtualNetworkMessageFraming
{
    public const int MaxMessageSize = 65535;

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > MaxMessageSize)
        {
            throw new InvalidDataException("Virtual Network transport message size is invalid.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        while (first < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(first, header.Length - first), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            first += read;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxMessageSize)
        {
            throw new InvalidDataException("Virtual Network transport message size is invalid.");
        }

        var payload = new byte[length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }

        return payload;
    }
}
