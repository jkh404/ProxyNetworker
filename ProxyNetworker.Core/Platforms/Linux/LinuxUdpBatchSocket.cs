using System.Buffers;
using System.Buffers.Binary;
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
    private readonly SafeSocketHandle _socketHandle;
    private readonly int _maxPacketSize;
    private readonly int _receiveBufferSize;
    private readonly int _socketBufferSize;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private readonly byte[][] _receiveBuffers = new byte[MaxBatchSize][];
    private readonly GCHandle[] _receivePins = new GCHandle[MaxBatchSize];
    private readonly IntPtr[] _receivePointers = new IntPtr[MaxBatchSize];
    private readonly LinuxUdpDatagram[] _received = new LinuxUdpDatagram[MaxBatchSize * LinuxUdpOffload.MaxGroSegments];
    private readonly IntPtr _receiveHeaders;
    private readonly IntPtr _receiveVectors;
    private readonly IntPtr _receiveNames;
    private readonly IntPtr _receiveControls;
    private readonly IntPtr _sendHeaders;
    private readonly IntPtr _sendVectors;
    private readonly IntPtr _sendNames;
    private readonly IntPtr _sendControls;
    private readonly GCHandle[] _sendPins = new GCHandle[MaxBatchSize];
    private readonly LinuxUdpSendGroup[] _sendGroups = new LinuxUdpSendGroup[MaxBatchSize];
    private readonly object _receiveLock = new();
    private readonly object _sendQueueLock = new();
    private readonly Queue<LinuxUdpOutboundDatagram> _sendQueue = new();
    private readonly CancellationTokenSource _sendCancellation = new();
    private readonly Task _sendLoop;
    private readonly bool _udpGroEnabled;
    private bool _udpGsoEnabled;
    private long _gsoDatagramsSent;
    private long _groDatagramsReceived;
    private int _disposed;
    private int _resourcesFreed;

    private LinuxUdpBatchSocket(
        Socket socket,
        int maxPacketSize,
        Action<ProxyNetworkerLogEntry>? logger,
        bool enableUdpGso,
        bool enableUdpGro)
    {
        _fileDescriptor = checked((int)socket.Handle);
        _socketHandle = socket.SafeHandle;
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
        try
        {
            SetNativeTimeouts(_socketHandle);
            _udpGsoEnabled = enableUdpGso && TrySetUdpOption(_socketHandle, LinuxUdpOffload.SegmentOption, 0);
            _udpGroEnabled = enableUdpGro && TrySetUdpOption(_socketHandle, LinuxUdpOffload.GroOption, 1);
            _receiveBufferSize = _udpGroEnabled
                ? Math.Max(maxPacketSize, LinuxUdpOffload.MaxGroBufferLength)
                : maxPacketSize;
            _receiveHeaders = Marshal.AllocHGlobal(Marshal.SizeOf<BatchMessageHeader>() * MaxBatchSize);
            _receiveVectors = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>() * MaxBatchSize);
            _receiveNames = Marshal.AllocHGlobal(SockAddrStorageSize * MaxBatchSize);
            _receiveControls = Marshal.AllocHGlobal(LinuxUdpOffload.ControlSpace * MaxBatchSize);
            _sendHeaders = Marshal.AllocHGlobal(Marshal.SizeOf<BatchMessageHeader>() * MaxBatchSize);
            _sendVectors = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>() * MaxBatchSize);
            _sendNames = Marshal.AllocHGlobal(SockAddrStorageSize * MaxBatchSize);
            _sendControls = Marshal.AllocHGlobal(LinuxUdpOffload.ControlSpace * MaxBatchSize);

            for (var index = 0; index < MaxBatchSize; index++)
            {
                _receiveBuffers[index] = GC.AllocateUninitializedArray<byte>(_receiveBufferSize);
                _receivePins[index] = GCHandle.Alloc(_receiveBuffers[index], GCHandleType.Pinned);
                _receivePointers[index] = _receivePins[index].AddrOfPinnedObject();
            }

            _sendLoop = Task.Factory.StartNew(
                () => SendLoop(_sendCancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch
        {
            FreeResources();
            throw;
        }
        Log(ProxyNetworkerLogLevel.Information,
            $"Linux UDP batch I/O enabled (recvmmsg/sendmmsg, batch={MaxBatchSize}, packetBuffer={_receiveBufferSize}, sendBuffer={_socketBufferSize}, UDP_SEGMENT={_udpGsoEnabled}, UDP_GRO={_udpGroEnabled}).");
    }

    internal int ReceivedCount { get; private set; }

    internal bool UdpGsoEnabled => _udpGsoEnabled;

    internal bool UdpGroEnabled => _udpGroEnabled;

    internal long GsoDatagramsSent => Interlocked.Read(ref _gsoDatagramsSent);

    internal long GroDatagramsReceived => Interlocked.Read(ref _groDatagramsReceived);

    internal LinuxUdpDatagram GetReceived(int index) => _received[index];

    public static LinuxUdpBatchSocket? TryCreate(
        Socket socket,
        int maxPacketSize,
        Action<ProxyNetworkerLogEntry>? logger = null,
        bool enableUdpGso = true,
        bool enableUdpGro = true)
    {
        if (!OperatingSystem.IsLinux() ||
            socket.AddressFamily != AddressFamily.InterNetwork ||
            socket.SafeHandle.IsInvalid ||
            maxPacketSize <= 0 ||
            IntPtr.Size != 8 ||
            !BitConverter.IsLittleEndian ||
            Marshal.SizeOf<MessageHeader>() != 56 ||
            Marshal.SizeOf<BatchMessageHeader>() != 64 ||
            !HasRequiredExports())
        {
            return null;
        }

        try
        {
            socket.Blocking = true;
            return new LinuxUdpBatchSocket(
                socket, maxPacketSize, logger,
                enableUdpGso && Environment.GetEnvironmentVariable("PN_DISABLE_UDP_GSO") != "1",
                enableUdpGro && Environment.GetEnvironmentVariable("PN_DISABLE_UDP_GRO") != "1");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or NotSupportedException or ArgumentException or SocketException)
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
        lock (_receiveLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            return ReceiveBatchCore(cancellationToken);
        }
    }

    private unsafe int ReceiveBatchCore(CancellationToken cancellationToken)
    {
        ReceivedCount = 0;
        var headers = (BatchMessageHeader*)_receiveHeaders;
        var vectors = (IoVector*)_receiveVectors;

        for (var index = 0; index < MaxBatchSize; index++)
        {
            var name = AddPointer(_receiveNames, index * SockAddrStorageSize);
            var vector = vectors + index;
            vector->Base = _receivePointers[index];
            vector->Length = (nuint)_receiveBufferSize;

            headers[index].Header.Name = name;
            headers[index].Header.NameLength = SockAddrStorageSize;
            headers[index].Header.IoVector = (IntPtr)vector;
            headers[index].Header.IoVectorCount = 1;
            headers[index].Header.Control = AddPointer(_receiveControls, index * LinuxUdpOffload.ControlSpace);
            headers[index].Header.ControlLength = LinuxUdpOffload.ControlSpace;
            headers[index].Header.Flags = 0;
            headers[index].Length = 0;
        }

        int received;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            received = recvmmsg(_socketHandle, headers, MaxBatchSize, MSG_WAITFORONE, IntPtr.Zero);
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

        for (var index = 0; index < received; index++)
        {
            var header = headers[index].Header;
            if ((header.Flags & LinuxUdpOffload.TruncatedFlags) != 0 ||
                header.ControlLength > LinuxUdpOffload.ControlSpace ||
                headers[index].Length > (uint)_receiveBufferSize)
            {
                continue;
            }

            var control = new ReadOnlySpan<byte>((void*)header.Control, (int)header.ControlLength);
            if (!LinuxUdpOffload.TryReadGroSegmentSize(control, out var segmentSize))
            {
                continue;
            }

            var length = (int)headers[index].Length;
            var segmentCount = LinuxUdpOffload.GetSegmentCount(length, segmentSize);
            if (segmentCount == 0)
            {
                continue;
            }

            var namePointer = headers[index].Header.Name;
            var nameLength = checked((int)headers[index].Header.NameLength);
            var remoteEndPoint = ParseEndPoint(namePointer, nameLength);
            var offset = 0;
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var packetLength = segmentSize == 0 ? length : Math.Min(segmentSize, length - offset);
                // A short final segment can be valid even when earlier segments exceed the configured limit.
                if (packetLength <= _maxPacketSize)
                {
                    _received[ReceivedCount++] = new LinuxUdpDatagram(_receiveBuffers[index], offset, packetLength, remoteEndPoint);
                }
                offset += packetLength;
            }

            if (segmentSize != 0)
            {
                Interlocked.Add(ref _groDatagramsReceived, segmentCount);
            }
        }

        return ReceivedCount;
    }

    internal void Enqueue(ReadOnlySpan<byte> datagram, IPEndPoint remoteEndPoint)
    {
        if (Volatile.Read(ref _disposed) != 0 || datagram.IsEmpty)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(buffer);
        if (!EnqueueOwned(buffer, datagram.Length, remoteEndPoint))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal bool EnqueueOwned(byte[] buffer, int length, IPEndPoint remoteEndPoint)
    {
        if (Volatile.Read(ref _disposed) != 0 || buffer is null || length <= 0 || length > buffer.Length)
        {
            return false;
        }

        lock (_sendQueueLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var wasEmpty = _sendQueue.Count == 0;
            _sendQueue.Enqueue(new LinuxUdpOutboundDatagram(buffer, length, remoteEndPoint));
            if (wasEmpty)
            {
                Monitor.Pulse(_sendQueueLock);
            }
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sendCancellation.Cancel();
        lock (_sendQueueLock)
        {
            Monitor.PulseAll(_sendQueueLock);
        }

        try
        {
            _sendLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException))
        {
        }
        finally
        {
            if (_sendLoop.IsCompleted)
            {
                FreeResources();
            }
            else
            {
                // A timed join must never free memory still used by a native send.
                _ = _sendLoop.ContinueWith(
                    _ => FreeResources(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private void FreeResources()
    {
        if (Interlocked.Exchange(ref _resourcesFreed, 1) != 0)
        {
            return;
        }

        lock (_receiveLock)
        {
            if (_udpGroEnabled)
            {
                try
                {
                    TrySetUdpOption(_socketHandle, LinuxUdpOffload.GroOption, 0);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            lock (_sendQueueLock)
            {
                while (_sendQueue.TryDequeue(out var pending))
                {
                    ArrayPool<byte>.Shared.Return(pending.Buffer);
                }
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
            Marshal.FreeHGlobal(_receiveControls);
            Marshal.FreeHGlobal(_sendHeaders);
            Marshal.FreeHGlobal(_sendVectors);
            Marshal.FreeHGlobal(_sendNames);
            Marshal.FreeHGlobal(_sendControls);
            _sendCancellation.Dispose();
        }
    }

    private void SendLoop(CancellationToken cancellationToken)
    {
        var batch = new LinuxUdpOutboundDatagram[MaxBatchSize];
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

            var sent = 0;
            try
            {
                while (sent < count && !cancellationToken.IsCancellationRequested)
                {
                    var submitted = SendBatch(batch.AsSpan(sent, count - sent), cancellationToken);
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
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                Log(ProxyNetworkerLogLevel.Warning, $"Linux sendmmsg failed; dropped {count - sent} UDP datagrams.", ex);
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

    // The send worker is the sole caller; native tests can submit a batch while its queue is idle.
    internal unsafe int SendBatch(ReadOnlySpan<LinuxUdpOutboundDatagram> batch, CancellationToken cancellationToken)
    {
        if (batch.IsEmpty || batch.Length > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batch));
        }

        var headers = (BatchMessageHeader*)_sendHeaders;
        var vectors = (IoVector*)_sendVectors;
        var pins = _sendPins;
        var groupCount = LinuxUdpOffload.BuildSendGroups(batch, _sendGroups, _udpGsoEnabled);
        var hasSegmentedGroup = false;
        try
        {
            for (var index = 0; index < batch.Length; index++)
            {
                var datagram = batch[index];
                pins[index] = GCHandle.Alloc(datagram.Buffer, GCHandleType.Pinned);
                var vector = vectors + index;
                vector->Base = pins[index].AddrOfPinnedObject();
                vector->Length = (nuint)datagram.Length;
            }

            for (var index = 0; index < groupCount; index++)
            {
                var group = _sendGroups[index];
                var datagram = batch[group.StartIndex];
                var name = AddPointer(_sendNames, index * SockAddrStorageSize);
                WriteSocketAddress(name, datagram.RemoteEndPoint);
                headers[index].Header.Name = name;
                headers[index].Header.NameLength = SockAddrInSize;
                headers[index].Header.IoVector = (IntPtr)(vectors + group.StartIndex);
                headers[index].Header.IoVectorCount = (nuint)group.PacketCount;
                headers[index].Header.Control = IntPtr.Zero;
                headers[index].Header.ControlLength = 0;
                headers[index].Header.Flags = 0;
                headers[index].Length = 0;
                if (group.PacketCount > 1)
                {
                    var control = AddPointer(_sendControls, index * LinuxUdpOffload.ControlSpace);
                    LinuxUdpOffload.WriteGsoControl(
                        new Span<byte>((void*)control, LinuxUdpOffload.ControlSpace), datagram.Length);
                    headers[index].Header.Control = control;
                    headers[index].Header.ControlLength = LinuxUdpOffload.ControlSpace;
                    hasSegmentedGroup = true;
                }
            }

            int sent;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                sent = sendmmsg(_socketHandle, headers, (uint)groupCount, 0);
            }
            while (sent < 0 && Marshal.GetLastPInvokeError() == EINTR);

            if (sent < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == EAGAIN)
                {
                    return 0;
                }
                if (hasSegmentedGroup && LinuxUdpOffload.ShouldDisableGso(error))
                {
                    _udpGsoEnabled = false;
                    Log(ProxyNetworkerLogLevel.Information,
                        $"Linux UDP_SEGMENT is unavailable on the active path (errno={error}); retrying unsent datagrams with plain sendmmsg.");
                    return 0;
                }

                throw new IOException($"Linux sendmmsg failed for UDP fd {_fileDescriptor}. errno={error}.");
            }

            for (var index = 0; index < sent; index++)
            {
                if (_sendGroups[index].PacketCount > 1)
                {
                    Interlocked.Add(ref _gsoDatagramsSent, _sendGroups[index].PacketCount);
                }
            }
            return LinuxUdpOffload.GetSubmittedPacketCount(_sendGroups.AsSpan(0, groupCount), sent);
        }
        finally
        {
            for (var index = 0; index < batch.Length; index++)
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
        if (!endPoint.Address.TryWriteBytes(span[4..8], out var written) || written != 4)
        {
            throw new ArgumentException("Linux UDP batch I/O requires an IPv4 endpoint.", nameof(endPoint));
        }
    }

    private static IntPtr AddPointer(IntPtr pointer, int offset) => IntPtr.Add(pointer, offset);

    private static bool HasRequiredExports()
    {
        if (!NativeLibrary.TryLoad("libc.so.6", out var libc) && !NativeLibrary.TryLoad("libc", out libc))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(libc, "recvmmsg", out _) &&
                NativeLibrary.TryGetExport(libc, "sendmmsg", out _) &&
                NativeLibrary.TryGetExport(libc, "setsockopt", out _);
        }
        finally
        {
            NativeLibrary.Free(libc);
        }
    }

    private static unsafe bool TrySetUdpOption(SafeSocketHandle socket, int option, int value) =>
        setsockopt(socket, LinuxUdpOffload.ProtocolUdp, option, &value, sizeof(int)) == 0;

    private static unsafe void SetNativeTimeouts(SafeSocketHandle socket)
    {
        // SO_RCVTIMEO bounds recvmmsg's initial wait; SafeHandle pins the descriptor during each syscall.
        var timeout = new TimeValue { Seconds = 0, Microseconds = 250_000 };
        if (setsockopt(socket, 1, 20, &timeout, (uint)sizeof(TimeValue)) != 0 ||
            setsockopt(socket, 1, 21, &timeout, (uint)sizeof(TimeValue)) != 0)
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }
    }

    private void Log(ProxyNetworkerLogLevel level, string message, Exception? exception = null)
    {
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message, exception));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeValue
    {
        public long Seconds;
        public long Microseconds;
    }

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
        SafeSocketHandle fileDescriptor,
        BatchMessageHeader* messages,
        uint messageCount,
        int flags,
        IntPtr timeout);

    [LibraryImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    private static unsafe partial int sendmmsg(
        SafeSocketHandle fileDescriptor,
        BatchMessageHeader* messages,
        uint messageCount,
        int flags);

    [LibraryImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static unsafe partial int setsockopt(
        SafeSocketHandle fileDescriptor, int level, int option, void* value, uint length);
}

internal readonly record struct LinuxUdpDatagram(byte[] Buffer, int Offset, int Length, IPEndPoint RemoteEndPoint)
{
    internal Memory<byte> Memory => Buffer.AsMemory(Offset, Length);
}
