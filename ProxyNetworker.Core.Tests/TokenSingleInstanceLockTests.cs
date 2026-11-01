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
}
