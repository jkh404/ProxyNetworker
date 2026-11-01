using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ProxyNetworker.Core.Platforms.Linux;

internal sealed partial class LinuxTunDevice : ITunDevice, ITunBatchDevice
{
    private const int O_RDWR = 2;
    private const int O_NONBLOCK = 0x800;
    private const short IFF_TUN = 0x0001;
    private const short IFF_NO_PI = 0x1000;
    private const short IFF_VNET_HDR = 0x4000;
    private const ulong TUNSETIFF = 0x400454ca;
    private const ulong TUNSETOFFLOAD = 0x400454d0;
    private const ulong TUNSETVNETHDRSZ = 0x400454d8;
    private const uint TUN_F_CSUM = 0x01;
    private const uint TUN_F_TSO4 = 0x02;
    private const int IFNAMSIZ = 16;
    private const int MaxPacketSize = 65535;
    private const int PollTimeoutMilliseconds = 500;
    private const short POLLIN = 0x0001;
    private const short POLLOUT = 0x0004;
    private const short POLLERR = 0x0008;
    private const short POLLHUP = 0x0010;
    private const short POLLNVAL = 0x0020;
    private const int EINTR = 4;
    private const int EAGAIN = 11;

    private readonly TunDeviceOptions _options;
    private readonly Action<ProxyNetworkerLogEntry>? _logger;
    private int _fileDescriptor = -1;
    private CancellationTokenSource? _readLoopCancellation;
    private Task? _readLoop;
    private readonly object _writeLock = new();
    private readonly byte[] _gsoSegmentBuffer = GC.AllocateUninitializedArray<byte>(MaxPacketSize * 2);
    private readonly byte[] _gsoAggregateBuffer = GC.AllocateUninitializedArray<byte>(MaxPacketSize);
    private readonly bool _gsoSendAggregationEnabled = !IsEnvironmentFlagEnabled("PN_DISABLE_GSO_TX");
    private int _vnetHeaderSize;
    private bool _gsoEnabled;
    private int _started;

    public LinuxTunDevice(TunDeviceOptions options, Action<ProxyNetworkerLogEntry>? logger)
    {
        _logger = logger;
        var normalizedName = LinuxInterfaceName.Normalize(options.Name);
        if (string.Equals(normalizedName, options.Name, StringComparison.Ordinal))
        {
            _options = options;
        }
        else
        {
            _options = CloneWithName(options, normalizedName);
            Log(ProxyNetworkerLogLevel.Information,
                $"Linux interface name '{options.Name}' exceeds the platform limit; using '{normalizedName}'.");
        }

        Name = _options.Name;
    }

    public string Name { get; private set; }

    public bool IsStarted => _started == 1;

    public event EventHandler<TunPacketReceivedEventArgs>? PacketReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new PlatformNotSupportedException("The Linux TUN backend currently configures IPv4 addresses only.");
        }

        ValidateLinuxInterfaceName(_options.Name);

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _vnetHeaderSize = 0;
        _gsoEnabled = false;

        var fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK);
        if (fd < 0)
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException($"Failed to open /dev/net/tun. errno={Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            var ifr = IfReq.Create(_options.Name, IFF_TUN | IFF_NO_PI | IFF_VNET_HDR);
            var vnetResult = ioctl(fd, TUNSETIFF, ref ifr);
            if (vnetResult < 0)
            {
                var vnetError = Marshal.GetLastPInvokeError();
                close(fd);
                fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK);
                if (fd < 0)
                {
                    throw new InvalidOperationException($"Failed to reopen /dev/net/tun after vnet_hdr fallback. errno={Marshal.GetLastPInvokeError()}.");
                }

                ifr = IfReq.Create(_options.Name, IFF_TUN | IFF_NO_PI);
                if (ioctl(fd, TUNSETIFF, ref ifr) < 0)
                {
                    throw new InvalidOperationException(
                        $"Failed to create Linux TUN device '{_options.Name}'. vnet_hdr errno={vnetError}, basic errno={Marshal.GetLastPInvokeError()}.");
                }

                Log(ProxyNetworkerLogLevel.Information,
                    $"Linux TUN vnet_hdr is unavailable; using the standard packet path (errno={vnetError}).");
            }
            else
            {
                var headerSize = LinuxTunGso.VirtioHeaderSize;
                if (ioctlInt(fd, TUNSETVNETHDRSZ, ref headerSize) == 0)
                {
                    _vnetHeaderSize = headerSize;
                    var offload = checked((int)(TUN_F_CSUM | TUN_F_TSO4));
                    if (ioctlValue(fd, TUNSETOFFLOAD, checked((nuint)offload)) == 0)
                    {
                        _gsoEnabled = true;
                    }
                    else
                    {
                        Log(ProxyNetworkerLogLevel.Warning,
                            $"Linux TUN vnet_hdr is active but TSO4 offload could not be enabled. errno={Marshal.GetLastPInvokeError()}.");
                    }
                }
                else
                {
                    Log(ProxyNetworkerLogLevel.Warning,
                        $"Linux TUN vnet_hdr header sizing failed; disabling virtio metadata. errno={Marshal.GetLastPInvokeError()}.");
                    close(fd);
                    fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK);
                    if (fd < 0)
                    {
                        throw new InvalidOperationException($"Failed to reopen /dev/net/tun after virtio header fallback. errno={Marshal.GetLastPInvokeError()}.");
                    }

                    ifr = IfReq.Create(_options.Name, IFF_TUN | IFF_NO_PI);
                    if (ioctl(fd, TUNSETIFF, ref ifr) < 0)
                    {
                        throw new InvalidOperationException($"Failed to recreate Linux TUN device '{_options.Name}' without vnet_hdr. errno={Marshal.GetLastPInvokeError()}.");
                    }
                }
            }

            Name = ifr.GetName();
            _fileDescriptor = fd;
            fd = -1;

            if (_options.ConfigureInterface)
            {
                await ConfigureInterfaceAsync(cancellationToken).ConfigureAwait(false);
            }

            _readLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoop = Task.Run(() => ReadLoop(_readLoopCancellation.Token));

            Log(ProxyNetworkerLogLevel.Information,
                $"Started Linux TUN device '{Name}' with {_options.Address}/{_options.PrefixLength}; vnet_hdr={_vnetHeaderSize > 0}, gso={_gsoEnabled}.");
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            var currentFd = Interlocked.Exchange(ref _fileDescriptor, -1);
            if (currentFd >= 0)
            {
                close(currentFd);
            }

            if (fd >= 0)
            {
                close(fd);
            }

            throw;
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (packet.IsEmpty)
        {
            throw new ArgumentException("Packet cannot be empty.", nameof(packet));
        }

        var fd = Volatile.Read(ref _fileDescriptor);
        if (fd < 0)
        {
            throw new InvalidOperationException("The Linux TUN device has not been started.");
        }

        lock (_writeLock)
        {
            WritePacket(fd, packet.Span, VirtioNetHeader.None, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> packets,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (packets.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var fd = Volatile.Read(ref _fileDescriptor);
        if (fd < 0)
        {
            throw new InvalidOperationException("The Linux TUN device has not been started.");
        }

        lock (_writeLock)
        {
            var index = 0;
            while (index < packets.Count)
            {
                if (_gsoEnabled && _gsoSendAggregationEnabled &&
                    LinuxTunGso.TryBuildTcpV4Aggregate(
                        packets,
                        index,
                        _gsoAggregateBuffer,
                        out var aggregate))
                {
                    var header = new VirtioNetHeader(
                        LinuxTunGso.FlagNeedsChecksum,
                        LinuxTunGso.GsoTcpV4,
                        checked((ushort)aggregate.HeaderLength),
                        checked((ushort)aggregate.GsoSize),
                        checked((ushort)aggregate.TcpOffset),
                        16);
                    WritePacket(
                        fd,
                        _gsoAggregateBuffer.AsSpan(0, aggregate.Length),
                        header,
                        cancellationToken);
                    index += aggregate.SegmentCount;
                    continue;
                }

                WritePacket(fd, packets[index].Span, VirtioNetHeader.None, cancellationToken);
                index++;
            }
        }

        return ValueTask.CompletedTask;
    }

    private unsafe void WritePacket(
        int fd,
        ReadOnlySpan<byte> packet,
        VirtioNetHeader header,
        CancellationToken cancellationToken)
    {
        if (packet.IsEmpty)
        {
            return;
        }

        if (_vnetHeaderSize <= 0)
        {
            WriteLegacyPacket(fd, packet, cancellationToken);
            return;
        }

        Span<byte> headerBytes = stackalloc byte[LinuxTunGso.VirtioHeaderSize];
        var vectors = stackalloc IoVector[2];
        LinuxTunGso.WriteHeader(headerBytes, header);
        while (true)
        {
            nint written;
            fixed (byte* headerPointer = headerBytes)
            fixed (byte* packetPointer = packet)
            {
                vectors[0].Base = (IntPtr)headerPointer;
                vectors[0].Length = LinuxTunGso.VirtioHeaderSize;
                vectors[1].Base = (IntPtr)packetPointer;
                vectors[1].Length = (nuint)packet.Length;
                written = writev(fd, vectors, 2);
            }

            if (written > 0)
            {
                if (written != LinuxTunGso.VirtioHeaderSize + packet.Length)
                {
                    throw new IOException($"Partial TUN write for '{Name}': wrote {written} of {LinuxTunGso.VirtioHeaderSize + packet.Length} bytes.");
                }

                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (written < 0 && error == EINTR)
            {
                continue;
            }

            if (written < 0 && error == EAGAIN)
            {
                if (!WaitForEvent(fd, POLLOUT, cancellationToken))
                {
                    return;
                }

                continue;
            }

            throw new IOException($"Failed to write virtio-net frame to Linux TUN device '{Name}'. errno={error}.");
        }
    }

    private void WriteLegacyPacket(int fd, ReadOnlySpan<byte> packet, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < packet.Length)
        {
            nint written;
            unsafe
            {
                fixed (byte* pointer = packet)
                {
                    written = write(fd, pointer + offset, (nuint)(packet.Length - offset));
                }
            }

            if (written > 0)
            {
                offset += checked((int)written);
                continue;
            }

            var error = Marshal.GetLastPInvokeError();
            if (written < 0 && error == EINTR)
            {
                continue;
            }

            if (written < 0 && error == EAGAIN)
            {
                if (!WaitForEvent(fd, POLLOUT, cancellationToken))
                {
                    return;
                }

                continue;
            }

            throw new IOException($"Failed to write to Linux TUN device '{Name}'. errno={error}.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        _readLoopCancellation?.Cancel();
        var fd = Interlocked.Exchange(ref _fileDescriptor, -1);
        if (fd >= 0)
        {
            close(fd);
        }

        try
        {
            _readLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static item => item is OperationCanceledException or ObjectDisposedException))
        {
        }

        _readLoopCancellation?.Dispose();
        _readLoopCancellation = null;
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(MaxPacketSize + LinuxTunGso.VirtioHeaderSize);
        var segmentRanges = GC.AllocateUninitializedArray<GsoSegment>((MaxPacketSize / 64) + 2);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var fd = Volatile.Read(ref _fileDescriptor);
                if (fd < 0)
                {
                    return;
                }

                nint length;
                unsafe
                {
                    fixed (byte* pointer = buffer)
                    {
                        length = read(fd, pointer, (nuint)buffer.Length);
                    }
                }

                if (length > 0)
                {
                    if (_vnetHeaderSize <= 0)
                    {
                        PacketReceived?.Invoke(this, new TunPacketReceivedEventArgs(buffer.AsMemory(0, checked((int)length))));
                        continue;
                    }

                    if (!LinuxTunGso.TryParseFrame(
                        buffer.AsSpan(0, checked((int)length)),
                        out var header,
                        out var payload))
                    {
                        Log(ProxyNetworkerLogLevel.Warning, "Dropped a malformed Linux TUN virtio-net frame.");
                        continue;
                    }

                    if ((header.GsoType & ~LinuxTunGso.GsoEcn) == 0)
                    {
                        if (!LinuxTunGso.TryCompleteChecksum(
                            buffer.AsSpan(LinuxTunGso.VirtioHeaderSize, checked((int)length) - LinuxTunGso.VirtioHeaderSize),
                            header))
                        {
                            if ((header.Flags & LinuxTunGso.FlagNeedsChecksum) != 0)
                            {
                                Log(ProxyNetworkerLogLevel.Warning, "Dropped a Linux TUN packet with malformed checksum metadata.");
                                continue;
                            }
                        }

                        PacketReceived?.Invoke(
                            this,
                            new TunPacketReceivedEventArgs(
                                buffer.AsMemory(LinuxTunGso.VirtioHeaderSize, checked((int)length) - LinuxTunGso.VirtioHeaderSize)));
                        continue;
                    }

                    if ((header.GsoType & ~LinuxTunGso.GsoEcn) != LinuxTunGso.GsoTcpV4 ||
                        !LinuxTunGso.TrySegmentTcpV4(
                            payload,
                            header.GsoSize,
                            _gsoSegmentBuffer,
                            segmentRanges,
                            out var segmentCount))
                    {
                        Log(
                            ProxyNetworkerLogLevel.Warning,
                            $"Dropped unsupported Linux TUN GSO frame (type=0x{header.GsoType:x2}, size={header.GsoSize}).");
                        continue;
                    }

                    for (var index = 0; index < segmentCount; index++)
                    {
                        var segment = segmentRanges[index];
                        PacketReceived?.Invoke(
                            this,
                            new TunPacketReceivedEventArgs(_gsoSegmentBuffer.AsMemory(segment.Offset, segment.Length)));
                    }

                    continue;
                }

                if (length == 0)
                {
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == EINTR)
                {
                    continue;
                }

                if (error == EAGAIN)
                {
                    if (!WaitForEvent(fd, POLLIN, cancellationToken))
                    {
                        return;
                    }

                    continue;
                }

                throw new IOException($"Failed to read from Linux TUN device '{Name}'. errno={error}.");
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Log(ProxyNetworkerLogLevel.Error, "Linux TUN read loop failed.", ex);
                }

                return;
            }
        }
    }

    private static bool WaitForEvent(int fd, short events, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pollFd = new PollFileDescriptor
            {
                FileDescriptor = fd,
                Events = events
            };
            var result = poll(ref pollFd, 1, PollTimeoutMilliseconds);
            if (result > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if ((pollFd.ReturnedEvents & (POLLERR | POLLHUP | POLLNVAL)) != 0)
                {
                    throw new IOException($"Poll failed for Linux TUN fd {fd}. revents={pollFd.ReturnedEvents}.");
                }

                return (pollFd.ReturnedEvents & events) != 0;
            }

            if (result == 0)
            {
                continue;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == EINTR)
            {
                continue;
            }

            throw new IOException($"Poll failed for Linux TUN fd {fd}. errno={error}.");
        }

        return false;
    }

    private async Task ConfigureInterfaceAsync(CancellationToken cancellationToken)
    {
        await RunCommandAsync("ip", $"addr replace {_options.Address}/{_options.PrefixLength} dev {Name}", cancellationToken).ConfigureAwait(false);
        await RunCommandAsync("ip", $"link set dev {Name} mtu {_options.Mtu} up", cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLinuxInterfaceName(string name)
    {
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(name);
        if (byteCount >= IFNAMSIZ)
        {
            throw new ArgumentException(
                $"Linux interface name '{name}' is {byteCount} bytes; the maximum is {IFNAMSIZ - 1} bytes.",
                nameof(name));
        }

        if (name.Any(static c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
        {
            throw new ArgumentException("Linux interface names can only contain letters, digits, '_', '-' and '.'.", nameof(name));
        }
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static TunDeviceOptions CloneWithName(TunDeviceOptions options, string name)
    {
        return new TunDeviceOptions
        {
            Name = name,
            TunnelType = options.TunnelType,
            Address = options.Address,
            PrefixLength = options.PrefixLength,
            Mtu = options.Mtu,
            WindowsAdapterId = options.WindowsAdapterId,
            WindowsSessionCapacity = options.WindowsSessionCapacity,
            ConfigureInterface = options.ConfigureInterface
        };
    }

    private async Task RunCommandAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{fileName} {arguments}' failed with exit code {process.ExitCode}. {stderr}{stdout}");
        }
    }

    private void Log(ProxyNetworkerLogLevel level, string message, Exception? exception = null)
    {
        _logger?.Invoke(new ProxyNetworkerLogEntry(level, message, exception));
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IfReq
    {
        public fixed byte Name[IFNAMSIZ];

        public short Flags;

        public fixed byte Padding[22];

        public static IfReq Create(string name, short flags)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            if (bytes.Length >= IFNAMSIZ)
            {
                throw new ArgumentException($"Linux interface names must be shorter than {IFNAMSIZ} bytes.", nameof(name));
            }

            var request = new IfReq
            {
                Flags = flags
            };

            for (var index = 0; index < bytes.Length; index++)
            {
                request.Name[index] = bytes[index];
            }

            return request;
        }

        public string GetName()
        {
            var length = 0;
            while (length < IFNAMSIZ && Name[length] != 0)
            {
                length++;
            }

            var bytes = new byte[length];
            for (var index = 0; index < length; index++)
            {
                bytes[index] = Name[index];
            }

            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFileDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public IntPtr Base;
        public nuint Length;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl(int fd, ulong request, ref IfReq ifr);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctlInt(int fd, ulong request, ref int value);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctlValue(int fd, ulong request, nuint value);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int poll(ref PollFileDescriptor fds, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "writev", SetLastError = true)]
    private static unsafe partial nint writev(int fd, IoVector* vectors, int count);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);
}
