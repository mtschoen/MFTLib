using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
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

        await harness.Source.WatchFor('T').Publish(new DriveCaughtUp());
        await wait.WaitAsync(ScriptedWatchSource.HangGuard);

        Assert.AreEqual(WatchCatchUpState.CaughtUp, harness.DriveFor('T').Watch.CatchUpState);
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

            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(
                () => wait.WaitAsync(ScriptedWatchSource.HangGuard));
        }
        finally
        {
            harness.Dispose();
        }
    }
}
