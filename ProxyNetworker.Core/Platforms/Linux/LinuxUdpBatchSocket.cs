using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ProxyNetworker.Core.Platforms.Linux;

internal sealed partial class LinuxUdpBatchSocket : IDisposable
{
    internal const int MaxBatchSize = 32;

    private const int EINTR = 4;
    private const int EAGAIN = 11;
    private const int EBADF = 9;
    private const int MSG_WAITFORONE = 0x10000;
    private const int SockAddrStorageSize = 128;
    private const int SockAddrInSize = 16;

    private readonly int _fileDescriptor;
    private readonly int _maxPacketSize;
    private readonly int _socketBufferSize;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly byte[][] _receiveBuffers;
    private readonly GCHandle[] _receivePins;
    private readonly IntPtr[] _receivePointers;
    private readonly LinuxUdpDatagram[] _received;
    private readonly IntPtr _receiveHeaders;
    private readonly IntPtr _receiveVectors;
    private readonly IntPtr _receiveNames;
    private readonly IntPtr _sendHeaders;
    private readonly IntPtr _sendVectors;
    private readonly IntPtr _sendNames;
    private readonly object _sendQueueLock = new();
    private readonly Queue<OutboundDatagram> _sendQueue = new();
    private readonly CancellationTokenSource _sendCancellation = new();
    private readonly Task _sendLoop;
    private int _disposed;

    private LinuxUdpBatchSocket(
        Socket socket,
        int maxPacketSize,
        Action<ProxyNetworkerLogEntry>? logger)
    {
        _fileDescriptor = checked((int)socket.Handle);
        _maxPacketSize = maxPacketSize;
        _logger = logger;
        try
        {
            socket.ReceiveBufferSize = Math.Max(socket.ReceiveBufferSize, 4 * 1024 * 1024);
            socket.SendBufferSize = Math.Max(socket.SendBufferSize, 4 * 1024 * 1024);
        }
        catch (SocketException)
        {
        }

        _socketBufferSize = socket.SendBufferSize;
        _receiveBuffers = new byte[MaxBatchSize][];
        _receivePins = new GCHandle[MaxBatchSize];
        _receivePointers = new IntPtr[MaxBatchSize];
        _received = new LinuxUdpDatagram[MaxBatchSize];
        _receiveHeaders = Marshal.AllocHGlobal(Marshal.SizeOf<BatchMessageHeader>() * MaxBatchSize);
        _receiveVectors = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>() * MaxBatchSize);
        _receiveNames = Marshal.AllocHGlobal(SockAddrStorageSize * MaxBatchSize);
        _sendHeaders = Marshal.AllocHGlobal(Marshal.SizeOf<BatchMessageHeader>() * MaxBatchSize);
        _sendVectors = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>() * MaxBatchSize);
        _sendNames = Marshal.AllocHGlobal(SockAddrStorageSize * MaxBatchSize);

        for (var index = 0; index < MaxBatchSize; index++)
        {
            _receiveBuffers[index] = GC.AllocateUninitializedArray<byte>(maxPacketSize);
            _receivePins[index] = GCHandle.Alloc(_receiveBuffers[index], GCHandleType.Pinned);
            _receivePointers[index] = _receivePins[index].AddrOfPinnedObject();
        }

        _sendLoop = Task.Factory.StartNew(
            () => SendLoop(_sendCancellation.Token),
            _sendCancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Log(ProxyNetworkerLogLevel.Information,
            $"Linux UDP batch I/O enabled (recvmmsg/sendmmsg, batch={MaxBatchSize}, packetBuffer={maxPacketSize}, sendBuffer={_socketBufferSize}).");
    }

    internal int ReceivedCount { get; private set; }

    internal LinuxUdpDatagram GetReceived(int index) => _received[index];

    public static LinuxUdpBatchSocket? TryCreate(
        Socket socket,
        int maxPacketSize,
        Action<ProxyNetworkerLogEntry>? logger = null)
    {
        if (!OperatingSystem.IsLinux() ||
            socket.AddressFamily != AddressFamily.InterNetwork ||
            socket.SafeHandle.IsInvalid ||
            maxPacketSize <= 0 ||
            Marshal.SizeOf<MessageHeader>() != 56 ||
            Marshal.SizeOf<BatchMessageHeader>() != 64 ||
            !HasRequiredExports())
        {
            return null;
        }

        try
        {
            socket.Blocking = true;
            return new LinuxUdpBatchSocket(socket, maxPacketSize, logger);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or NotSupportedException or ArgumentException)
        {
            logger?.Invoke(new ProxyNetworkerLogEntry(
                ProxyNetworkerLogLevel.Debug,
                "Linux UDP batch I/O is unavailable; falling back to single-datagram socket operations.",
                ex));
            return null;
        }
    }

    internal unsafe int ReceiveBatch(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = (BatchMessageHeader*)_receiveHeaders;
        var vectors = (IoVector*)_receiveVectors;

        for (var index = 0; index < MaxBatchSize; index++)
        {
            var name = AddPointer(_receiveNames, index * SockAddrStorageSize);
            var vector = vectors + index;
            vector->Base = _receivePointers[index];
            vector->Length = (nuint)_maxPacketSize;

            headers[index].Header.Name = name;
            headers[index].Header.NameLength = SockAddrStorageSize;
            headers[index].Header.IoVector = (IntPtr)vector;
            headers[index].Header.IoVectorCount = 1;
            headers[index].Header.Control = IntPtr.Zero;
            headers[index].Header.ControlLength = 0;
            headers[index].Header.Flags = 0;
            headers[index].Length = 0;
        }

        int received;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            received = recvmmsg(_fileDescriptor, headers, MaxBatchSize, MSG_WAITFORONE, IntPtr.Zero);
        }
        while (received < 0 && Marshal.GetLastPInvokeError() == EINTR && !cancellationToken.IsCancellationRequested);

        if (received < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is EAGAIN or EBADF)
            {
                return 0;
            }

            throw new IOException($"Linux recvmmsg failed for UDP fd {_fileDescriptor}. errno={error}.");
        }

        ReceivedCount = received;
        for (var index = 0; index < received; index++)
        {
            var namePointer = headers[index].Header.Name;
            var nameLength = checked((int)headers[index].Header.NameLength);
            var remoteEndPoint = ParseEndPoint(namePointer, nameLength);
            var length = checked((int)headers[index].Length);
            if (length < 0 || length > _maxPacketSize)
            {
                length = 0;
            }

            _received[index] = new LinuxUdpDatagram(_receiveBuffers[index], length, remoteEndPoint);
        }

        return received;
    }

    internal void Enqueue(ReadOnlySpan<byte> datagram, IPEndPoint remoteEndPoint)
    {
        if (Volatile.Read(ref _disposed) != 0 || datagram.IsEmpty)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(buffer);
        lock (_sendQueueLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return;
            }

            _sendQueue.Enqueue(new OutboundDatagram(buffer, datagram.Length, remoteEndPoint));
            Monitor.Pulse(_sendQueueLock);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sendQueueLock)
        {
            Monitor.PulseAll(_sendQueueLock);
        }

        _sendCancellation.Cancel();
        try
        {
            _sendLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException))
        {
        }

        while (_sendQueue.TryDequeue(out var pending))
        {
            ArrayPool<byte>.Shared.Return(pending.Buffer);
        }

        for (var index = 0; index < MaxBatchSize; index++)
        {
            if (_receivePins[index].IsAllocated)
            {
                _receivePins[index].Free();
            }
        }

        Marshal.FreeHGlobal(_receiveHeaders);
        Marshal.FreeHGlobal(_receiveVectors);
        Marshal.FreeHGlobal(_receiveNames);
        Marshal.FreeHGlobal(_sendHeaders);
        Marshal.FreeHGlobal(_sendVectors);
        Marshal.FreeHGlobal(_sendNames);
        _sendCancellation.Dispose();
    }

    private void SendLoop(CancellationToken cancellationToken)
    {
        var batch = new OutboundDatagram[MaxBatchSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = 0;
            lock (_sendQueueLock)
            {
                while (_sendQueue.Count == 0 && !cancellationToken.IsCancellationRequested)
                {
                    Monitor.Wait(_sendQueueLock, 500);
                }

                while (count < batch.Length && _sendQueue.TryDequeue(out var datagram))
                {
                    batch[count++] = datagram;
                }
            }

            if (count == 0)
            {
                continue;
            }

            try
            {
                var sent = 0;
                while (sent < count && !cancellationToken.IsCancellationRequested)
                {
                    var submitted = SendBatch(batch, sent, count - sent);
                    if (submitted <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    sent += submitted;
                    if (sent < count)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                Log(ProxyNetworkerLogLevel.Warning, $"Linux sendmmsg failed; dropped {count} UDP datagrams.", ex);
            }
            finally
            {
                for (var index = 0; index < count; index++)
                {
                    ArrayPool<byte>.Shared.Return(batch[index].Buffer);
                    batch[index] = default;
                }
            }
        }
    }

    private unsafe int SendBatch(OutboundDatagram[] batch, int startIndex, int count)
    {
        var headers = (BatchMessageHeader*)_sendHeaders;
        var vectors = (IoVector*)_sendVectors;
        var pins = new GCHandle[count];
        try
        {
            for (var index = 0; index < count; index++)
            {
                var datagram = batch[startIndex + index];
                pins[index] = GCHandle.Alloc(datagram.Buffer, GCHandleType.Pinned);
                var vector = vectors + index;
                vector->Base = pins[index].AddrOfPinnedObject();
                vector->Length = (nuint)datagram.Length;

                var name = AddPointer(_sendNames, index * SockAddrStorageSize);
                WriteSocketAddress(name, datagram.RemoteEndPoint);
                headers[index].Header.Name = name;
                headers[index].Header.NameLength = SockAddrInSize;
                headers[index].Header.IoVector = (IntPtr)vector;
                headers[index].Header.IoVectorCount = 1;
                headers[index].Header.Control = IntPtr.Zero;
                headers[index].Header.ControlLength = 0;
                headers[index].Header.Flags = 0;
                headers[index].Length = 0;
            }

            int sent;
            do
            {
                sent = sendmmsg(_fileDescriptor, headers, (uint)count, 0);
            }
            while (sent < 0 && Marshal.GetLastPInvokeError() == EINTR);

            if (sent < 0)
            {
                throw new IOException($"Linux sendmmsg failed for UDP fd {_fileDescriptor}. errno={Marshal.GetLastPInvokeError()}.");
            }

            return sent;
        }
        finally
        {
            for (var index = 0; index < count; index++)
            {
                if (pins[index].IsAllocated)
                {
                    pins[index].Free();
                }
            }
        }
    }

    private static unsafe IPEndPoint ParseEndPoint(IntPtr socketAddress, int length)
    {
        if (length < SockAddrInSize)
        {
            return new IPEndPoint(IPAddress.Any, 0);
        }

        var span = new ReadOnlySpan<byte>((void*)socketAddress, SockAddrInSize);
        var port = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
        return new IPEndPoint(new IPAddress(span[4..8]), port);
    }

    private static unsafe void WriteSocketAddress(IntPtr destination, IPEndPoint endPoint)
    {
        var span = new Span<byte>((void*)destination, SockAddrStorageSize);
        span.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(span[..2], (ushort)AddressFamily.InterNetwork);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], checked((ushort)endPoint.Port));
        endPoint.Address.GetAddressBytes().CopyTo(span[4..8]);
    }

    private static IntPtr AddPointer(IntPtr pointer, int offset) => IntPtr.Add(pointer, offset);

    private static bool HasRequiredExports()
    {
        if (!NativeLibrary.TryLoad("libc.so.6", out var libc) && !NativeLibrary.TryLoad("libc", out libc))
        {
            return false;
        }

        return NativeLibrary.TryGetExport(libc, "recvmmsg", out _) &&
            NativeLibrary.TryGetExport(libc, "sendmmsg", out _);
    }

    private void Log(ProxyNetworkerLogLevel level, string message, Exception? exception = null)
    {
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message, exception));
    }

    private readonly record struct OutboundDatagram(byte[] Buffer, int Length, IPEndPoint RemoteEndPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public IntPtr Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageHeader
    {
        public IntPtr Name;
        public uint NameLength;
        public IntPtr IoVector;
        public nuint IoVectorCount;
        public IntPtr Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatchMessageHeader
    {
        public MessageHeader Header;
        public uint Length;
    }

    [LibraryImport("libc", EntryPoint = "recvmmsg", SetLastError = true)]
    private static unsafe partial int recvmmsg(
        int fileDescriptor,
        BatchMessageHeader* messages,
        uint messageCount,
        int flags,
        IntPtr timeout);

    [LibraryImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    private static unsafe partial int sendmmsg(
        int fileDescriptor,
        BatchMessageHeader* messages,
        uint messageCount,
        int flags);
}

internal readonly record struct LinuxUdpDatagram(byte[] Buffer, int Length, IPEndPoint RemoteEndPoint);
