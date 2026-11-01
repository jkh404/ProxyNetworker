using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ProxyNetworker.Core.Platforms.Linux;

internal sealed partial class LinuxTunDevice : ITunDevice
{
    private const int O_RDWR = 2;
    private const int O_NONBLOCK = 0x800;
    private const short IFF_TUN = 0x0001;
    private const short IFF_NO_PI = 0x1000;
    private const ulong TUNSETIFF = 0x400454ca;
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

        var fd = open("/dev/net/tun", O_RDWR | O_NONBLOCK);
        if (fd < 0)
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException($"Failed to open /dev/net/tun. errno={Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            var ifr = IfReq.Create(_options.Name, IFF_TUN | IFF_NO_PI);

            if (ioctl(fd, TUNSETIFF, ref ifr) < 0)
            {
                throw new InvalidOperationException($"Failed to create Linux TUN device '{_options.Name}'. errno={Marshal.GetLastPInvokeError()}.");
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

            Log(ProxyNetworkerLogLevel.Information, $"Started Linux TUN device '{Name}' with {_options.Address}/{_options.PrefixLength}.");
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

        var buffer = packet.ToArray();
        var offset = 0;
        while (offset < buffer.Length)
        {
            if (!WaitForEvent(fd, POLLOUT, cancellationToken))
            {
                return ValueTask.CompletedTask;
            }

            nint written;
            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    written = write(fd, pointer + offset, (nuint)(buffer.Length - offset));
                }
            }

            if (written > 0)
            {
                offset += checked((int)written);
                continue;
            }

            var error = Marshal.GetLastPInvokeError();
            if (written < 0 && error is EINTR or EAGAIN)
            {
                continue;
            }

            throw new IOException($"Failed to write to Linux TUN device '{Name}'. errno={error}.");
        }

        return ValueTask.CompletedTask;
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
        var buffer = new byte[MaxPacketSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var fd = Volatile.Read(ref _fileDescriptor);
                if (fd < 0 || !WaitForEvent(fd, POLLIN, cancellationToken))
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
                    var packet = new byte[(int)length];
                    Buffer.BlockCopy(buffer, 0, packet, 0, packet.Length);
                    PacketReceived?.Invoke(this, new TunPacketReceivedEventArgs(packet));
                    continue;
                }

                if (length == 0)
                {
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error is EINTR or EAGAIN)
                {
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

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl(int fd, ulong request, ref IfReq ifr);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int poll(ref PollFileDescriptor fds, nuint count, int timeout);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);
}
