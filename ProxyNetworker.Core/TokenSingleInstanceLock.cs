using System.Security.Cryptography;
using System.Text;

namespace ProxyNetworker.Core;

internal sealed class TokenSingleInstanceLock : IDisposable
{
    private static readonly string LockDirectory = ResolveLockDirectory();

    private readonly FileStream _stream;
    private bool _disposed;

    private TokenSingleInstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static TokenSingleInstanceLock? TryAcquire(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Directory.CreateDirectory(LockDirectory);
        var path = GetLockFilePath(token);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1024, FileOptions.WriteThrough);
            }
            catch (UnauthorizedAccessException) when (File.Exists(path))
            {
                // Elevated clients can leave read-only ACLs. Exclusivity does not require updating PID metadata.
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            try
            {
                if (stream.CanWrite)
                {
                    var metadata = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\n");
                    stream.SetLength(0);
                    stream.Write(metadata);
                    stream.Flush(flushToDisk: true);
                }
                return new TokenSingleInstanceLock(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    internal static string GetLockFilePath(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Path.Combine(LockDirectory, Convert.ToHexString(hash) + ".lock");
    }

    private static string ResolveLockDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonApplicationData))
            {
                return Path.Combine(commonApplicationData, "ProxyNetworker", "locks");
            }
        }

        return Path.Combine(Path.GetTempPath(), "proxynetworker-client-locks");
    }
}
