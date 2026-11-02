using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProxyNetworker.Core.Platforms.MacOS;

internal sealed class MacOsTunConnection : IMacOsTunConnection
{
    private const int Interrupted = 4;
    private const int WouldBlock = 35;
    private const short Readable = 0x0001;
    private const short Writable = 0x0004;
    private const short PollFailure = 0x0008 | 0x0010 | 0x0020;

    private readonly SafeUtunHandle _handle;
    private readonly object _writeLock = new();
    private int _disposed;

    private MacOsTunConnection(SafeUtunHandle handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    public string Name { get; }

    internal static unsafe MacOsTunConnection Create()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The utun control socket is available only on macOS.");
        }

        var descriptor = MacOsTunNative.Socket(32, 2, 2);
        if (descriptor < 0)
        {
            throw Error("create the utun control socket");
        }

        var handle = new SafeUtunHandle(descriptor);
        try
        {
            var info = new MacOsTunNative.ControlInfo();
            var control = "com.apple.net.utun_control"u8;
            for (var index = 0; index < control.Length; index++)
            {
                info.Name[index] = control[index];
            }

            if (MacOsTunNative.GetControlInfo(descriptor, ref info) < 0)
            {
                throw Error("resolve the utun kernel control");
            }

            var address = new MacOsTunNative.ControlAddress
            {
                Length = 32,
                Family = 32,
                SystemAddress = 2,
                Id = info.Id,
                Unit = 0
            };
            if (MacOsTunNative.Connect(descriptor, ref address, 32) < 0)
            {
                throw Error("connect the utun control socket (administrator privileges are required)");
            }

            if (MacOsTunNative.PrepareSocket(descriptor) < 0)
            {
                throw Error("set nonblocking and close-on-exec flags on utun");
            }

            Span<byte> nameBytes = stackalloc byte[16];
            uint length = 16;
            fixed (byte* pointer = nameBytes)
            {
                if (MacOsTunNative.GetSocketOption(descriptor, 2, 2, pointer, ref length) < 0)
                {
                    throw Error("read the actual utun interface name");
                }
            }

            if (length is 0 or > 16)
            {
                throw new IOException("The kernel returned an invalid utun interface-name length.");
            }

            var end = nameBytes[..(int)length].IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nameBytes[..(end < 0 ? (int)length : end)]);
            if (!MacOsTunConfiguration.IsInterfaceName(name))
            {
                throw new IOException("The kernel did not return a valid utun interface name.");
            }

            return new MacOsTunConnection(handle, name);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public unsafe int ReadFrame(Span<byte> buffer, CancellationToken cancellationToken)
    {
        var reference = false;
        try
        {
            Check(cancellationToken);
            _handle.DangerousAddRef(ref reference);
            var descriptor = checked((int)_handle.DangerousGetHandle());
            while (true)
            {
                Check(cancellationToken);
                nint length;
                fixed (byte* pointer = buffer)
                {
                    length = MacOsTunNative.Read(descriptor, pointer, (nuint)buffer.Length);
                }

                if (length >= 0)
                {
                    return checked((int)length);
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == Interrupted)
                {
                    continue;
                }

                if (error == WouldBlock)
                {
                    Wait(descriptor, Readable, cancellationToken);
                    continue;
                }

                throw Error("read a utun packet", error);
            }
        }
        finally
        {
            if (reference)
            {
                _handle.DangerousRelease();
            }
        }
    }

    public unsafe void WritePacket(ReadOnlySpan<byte> packet, CancellationToken cancellationToken)
    {
        lock (_writeLock)
        {
            var reference = false;
            try
            {
                Check(cancellationToken);
                _handle.DangerousAddRef(ref reference);
                var descriptor = checked((int)_handle.DangerousGetHandle());
                Span<byte> header = stackalloc byte[MacOsUtunPacket.HeaderSize];
                MacOsUtunPacket.WriteHeader(header);
                var vectors = stackalloc MacOsTunNative.IoVector[2];
                while (true)
                {
                    Check(cancellationToken);
                    nint written;
                    fixed (byte* headerPointer = header)
                    fixed (byte* packetPointer = packet)
                    {
                        vectors[0] = new() { Base = (nint)headerPointer, Length = (nuint)header.Length };
                        vectors[1] = new() { Base = (nint)packetPointer, Length = (nuint)packet.Length };
                        written = MacOsTunNative.WriteVectors(descriptor, vectors, 2);
                    }

                    if (written >= 0)
                    {
                        // A utun write is one packet; retrying a short write as a tail corrupts framing.
                        if (written != header.Length + packet.Length)
                        {
                            throw new IOException($"Partial packet write on {Name}: {written} bytes.");
                        }

                        return;
                    }

                    var error = Marshal.GetLastPInvokeError();
                    if (error == Interrupted)
                    {
                        continue;
                    }

                    if (error == WouldBlock)
                    {
                        Wait(descriptor, Writable, cancellationToken);
                        continue;
                    }

                    throw Error("write a utun packet", error);
                }
            }
            finally
            {
                if (reference)
                {
                    _handle.DangerousRelease();
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Closing the last control socket detaches its utun and interface-owned routes.
            _handle.Dispose();
        }
    }

    private void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void Wait(int descriptor, short events, CancellationToken cancellationToken)
    {
        while (true)
        {
            Check(cancellationToken);
            var item = new MacOsTunNative.PollDescriptor { Descriptor = descriptor, Events = events };
            var result = MacOsTunNative.Poll(ref item, 1, 100);
            if (result > 0)
            {
                Check(cancellationToken);
                if ((item.ReturnedEvents & PollFailure) != 0)
                {
                    throw new IOException($"utun poll failed on {Name}: flags={item.ReturnedEvents}.");
                }

                if ((item.ReturnedEvents & events) != 0)
                {
                    return;
                }
            }
            else if (result < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != Interrupted)
                {
                    throw Error("poll the utun socket", error);
                }
            }
        }
    }

    private static IOException Error(string operation, int? error = null) =>
        new($"Could not {operation}. Darwin errno={error ?? Marshal.GetLastPInvokeError()}.");

    private sealed class SafeUtunHandle : SafeHandleMinusOneIsInvalid
    {
        internal SafeUtunHandle(int descriptor) : base(ownsHandle: true)
        {
            SetHandle(descriptor);
        }

        protected override bool ReleaseHandle() => MacOsTunNative.Close(checked((int)handle)) == 0;
    }
}

internal static partial class MacOsTunNative
{
    private const string Library = "/usr/lib/libSystem.B.dylib";

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ControlInfo
    {
        internal uint Id;
        internal fixed byte Name[96];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ControlAddress
    {
        internal byte Length;
        internal byte Family;
        internal ushort SystemAddress;
        internal uint Id;
        internal uint Unit;
        internal fixed uint Reserved[5];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollDescriptor
    {
        internal int Descriptor;
        internal short Events;
        internal short ReturnedEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoVector
    {
        internal nint Base;
        internal nuint Length;
    }

    [LibraryImport(Library, EntryPoint = "socket", SetLastError = true)]
    internal static partial int Socket(int domain, int type, int protocol);

    [LibraryImport("proxynetworker-utun", EntryPoint = "pn_utun_control_info", SetLastError = true)]
    internal static partial int GetControlInfo(int descriptor, ref ControlInfo info);

    [LibraryImport(Library, EntryPoint = "connect", SetLastError = true)]
    internal static partial int Connect(int descriptor, ref ControlAddress address, uint length);

    [LibraryImport(Library, EntryPoint = "getsockopt", SetLastError = true)]
    internal static unsafe partial int GetSocketOption(int descriptor, int level, int option, byte* value, ref uint length);

    [LibraryImport("proxynetworker-utun", EntryPoint = "pn_utun_prepare_socket", SetLastError = true)]
    internal static partial int PrepareSocket(int descriptor);

    [LibraryImport(Library, EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial nint Read(int descriptor, byte* buffer, nuint length);

    [LibraryImport(Library, EntryPoint = "writev", SetLastError = true)]
    internal static unsafe partial nint WriteVectors(int descriptor, IoVector* vectors, int count);

    [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
    internal static partial int Poll(ref PollDescriptor descriptor, uint count, int timeout);

    [LibraryImport(Library, EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int descriptor);
}
