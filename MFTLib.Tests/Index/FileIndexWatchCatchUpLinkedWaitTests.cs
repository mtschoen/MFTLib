using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The no-caller-token branch of the catch-up wait: with <see cref="CancellationToken.None" />
///     the wait is linked to the index's disposal token only, so a clean catch-up completes it and
///     a disposal cancels it.
/// </summary>
[TestClass]
public class FileIndexWatchCatchUpLinkedWaitTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task WaitForCatchUpAsync_WithoutACallerToken_CompletesWithTheCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync('T', Token);

        var wait = harness.Index.WaitForCatchUpAsync('T', CancellationToken.None);
        Assert.IsFalse(wait.IsCompleted);

        await harness.Source.HandleFor('T').Publish(new DriveCaughtUp());
        await wait.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').WatchCatchUp);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task WaitForCatchUpAsync_WithoutACallerToken_WhenDisposed_CancelsTheWait()
    {
        var harness = new WatchHarness();
        try
        {
            await harness.Index.StartWatchingAsync('T', Token);

            var wait = harness.Index.WaitForCatchUpAsync('T', CancellationToken.None);
            Assert.IsFalse(wait.IsCompleted);

            await harness.Index.DisposeAsync();

            try
            {
                await wait.WaitAsync(FakeIndexWatchSource.HangGuard);
                Assert.Fail("Expected an OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected: the disposal token cancelled the linked wait.
            }
        }
        finally
        {
            harness.Dispose();
        }
    }
}
