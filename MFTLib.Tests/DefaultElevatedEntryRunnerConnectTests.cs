using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class DefaultElevatedEntryRunnerConnectTests
{
    [TestMethod]
    public async Task ConnectVolumePipeAsync_CancelledBeforeAnyServerListens_ThrowsOperationCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var pipeName = "mftlib-connect-test-" + Guid.NewGuid().ToString("N");

        try
        {
            await DefaultElevatedEntryRunner.ConnectVolumePipeAsync(
                pipeName, BrokerLiveness.ElevatedConnectTimeout, TimeProvider.System, cancellation.Token)
                .WaitAsync(HangGuard, TestContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected the cancelled connect to throw.");
    }

    public TestContext TestContext { get; set; } = null!;
}
