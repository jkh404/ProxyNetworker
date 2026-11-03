using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class TokenSingleInstanceLockTests
{
    [Fact]
    public void SameTokenCannotBeAcquiredTwice()
    {
        var token = "vnet_" + Guid.NewGuid().ToString("N");
        var path = TokenSingleInstanceLock.GetLockFilePath(token);

        try
        {
            using var first = TokenSingleInstanceLock.TryAcquire(token);
            Assert.NotNull(first);
            Assert.Null(TokenSingleInstanceLock.TryAcquire(token));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LockCanBeReacquiredAfterDispose()
    {
        var token = "ptun_" + Guid.NewGuid().ToString("N");
        var path = TokenSingleInstanceLock.GetLockFilePath(token);

        try
        {
            using (var first = TokenSingleInstanceLock.TryAcquire(token))
            {
                Assert.NotNull(first);
            }

            using var second = TokenSingleInstanceLock.TryAcquire(token);
            Assert.NotNull(second);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ReadOnlyLockStillExcludesAnotherClient()
    {
        var token = "ptun_" + Guid.NewGuid().ToString("N");
        var path = TokenSingleInstanceLock.GetLockFilePath(token);
        try
        {
            using (var original = TokenSingleInstanceLock.TryAcquire(token))
                Assert.NotNull(original);
            var before = File.ReadAllText(path);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            using (var lease = TokenSingleInstanceLock.TryAcquire(token))
            {
                Assert.NotNull(lease);
                Assert.Null(TokenSingleInstanceLock.TryAcquire(token));
            }
            if (OperatingSystem.IsWindows()) Assert.Equal(before, File.ReadAllText(path));
            using var next = TokenSingleInstanceLock.TryAcquire(token);
            Assert.NotNull(next);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }
    }
}
