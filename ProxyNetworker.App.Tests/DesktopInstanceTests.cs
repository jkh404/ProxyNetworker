using ProxyNetworker.App.Desktop;

namespace ProxyNetworker.App.Tests;

public sealed class DesktopInstanceTests
{
    [Fact]
    public async Task SecondInstanceActivatesTheFirstAndTheLeaseIsReleased()
    {
        var directory = Directory.CreateTempSubdirectory("pn-instance-");
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var first = DesktopInstance.TryAcquire(directory.FullName, () => activated.TrySetResult());
            Assert.NotNull(first);
            Assert.Null(DesktopInstance.TryAcquire(directory.FullName, () => { }));
            Assert.True(await DesktopInstance.ActivateExistingAsync(directory.FullName));
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            first.Dispose();
            using var replacement = DesktopInstance.TryAcquire(directory.FullName, () => { });
            Assert.NotNull(replacement);
        }
        finally
        {
            File.Delete(Path.Combine(directory.FullName, "ui.lock"));
            directory.Delete();
        }
    }

    [Fact]
    public async Task ActivationWithoutAnInstanceHasABoundedTimeout()
    {
        var directory = Directory.CreateTempSubdirectory("pn-instance-");
        try { Assert.False(await DesktopInstance.ActivateExistingAsync(directory.FullName)); }
        finally { directory.Delete(); }
    }
}
