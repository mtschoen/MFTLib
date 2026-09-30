using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class DefaultElevatedEntryRunnerConnectTests
{
    [TestMethod]
    public async Task ConnectDrivePipeAsync_CancelledBeforeAnyServerListens_ThrowsOperationCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var pipeName = "mftlib-connect-test-" + Guid.NewGuid().ToString("N");

        try
        {
            await DefaultElevatedEntryRunner.ConnectDrivePipeAsync(pipeName, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected the cancelled connect to throw.");
    }
}
