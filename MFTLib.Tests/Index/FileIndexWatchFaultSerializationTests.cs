using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>One drive's <see cref="FileIndex.WatchFaulted" /> callbacks never overlap, even across a recovery.</summary>
[TestClass]
public class FileIndexWatchFaultSerializationTests
{
    public TestContext TestContext { get; set; } = null!;
    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task SuccessorFaultDuringRecoveryFaultHandler_DoesNotOverlapIt()
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.FailNextProduction('T', new IOException("the volume went away"));
        var recoveryHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecoveryHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? successorPump = null;
        bool recoveryHandlerReturned = false, overlapped = false;
        harness.Index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Recovery)
            {
                recoveryHandlerEntered.SetResult();
                releaseRecoveryHandler.Task.Wait(Token);
                recoveryHandlerReturned = true;
            }
            else if (fault.Kind == WatchFaultKind.Channel)
            {
                overlapped = !recoveryHandlerReturned;
                successorHandlerEntered.SetResult();
            }
        };
        harness.Index.WatchStateChanged += change => successorPump = change.Fault?.Kind == WatchFaultKind.Channel
            ? Thread.CurrentThread
            : successorPump;

        harness.Source.WatchFor('T').FailDrive(new IOException("T's journal wrapped"));
        await recoveryHandlerEntered.Task.WaitAsync(HangGuard, Token);
        await harness.IndexFor('T').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').LoseChannel(new IOException("the successor's broker died"));

        // Serialized, the successor's pump (captured by its state change) blocks; overlapped, its handler enters.
        using var hangGuard = new CancellationTokenSource(HangGuard);
        while (!successorHandlerEntered.Task.IsCompleted && successorPump?.ThreadState.HasFlag(ThreadState.WaitSleepJoin) != true)
        {
            hangGuard.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        releaseRecoveryHandler.SetResult();
        await successorHandlerEntered.Task.WaitAsync(HangGuard, Token);

        Assert.IsFalse(overlapped, "the successor's fault was delivered while the recovery's fault handler ran");
    }
}
