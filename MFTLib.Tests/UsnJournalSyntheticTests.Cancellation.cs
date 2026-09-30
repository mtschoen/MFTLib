using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class UsnJournalSyntheticTests
{
    [DataTestMethod]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    public async Task CloseWatchPipe_IdleNativeRead_EndsChannel(bool cancelBeforeIssue, int readNumber)
    {
        var pipe = await IdleUsnPipe.CreateAsync(readNumber);
        try
        {
            FileUtilities._getVolumeHandle = pipe.BorrowHandle;
            QueueSuccess(BuildQueryBuffer(journalId: 7, nextUsn: 200));
            var cancellationAttempted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeCancel = MFTLibNative._cancelUsnJournalWatch;
            MFTLibNative._cancelUsnJournalWatch = handle =>
            {
                var cancelled = nativeCancel(handle);
                cancellationAttempted.TrySetResult(cancelled);
                return cancelled;
            };

            // The host's clock never advances, so the session can only end once the watch channel
            // has really stopped: the grace period that would otherwise end it never passes.
            var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault(new FakeTimeProvider()));
            try
            {
                var drivePipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 200));
                Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(drivePipe))?.Kind);
                if (readNumber == 2)
                {
                    await pipe.SendEmptyBatchAsync(201);
                }
                await IdleUsnPipe.AwaitSignalAsync(pipe.BeforeIssue);
                if (!cancelBeforeIssue)
                {
                    pipe.ContinueIssue.Set();
                    await IdleUsnPipe.AwaitSignalAsync(pipe.Issued);
                }

                // Closing the drive pipe is how a client stops a watch.
                await drivePipe.DisposeAsync();
                var foundRequest = await cancellationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cancelBeforeIssue)
                {
                    Assert.IsFalse(foundRequest, "The regression must cancel before a request exists.");
                }
                pipe.ContinueIssue.Set();
                await harness.CloseControlAsync();
                await harness.Serve.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                pipe.UnblockForCleanup();
                await harness.DisposeAsync();
            }
        }
        finally
        {
            FileUtilities.ResetToDefaults();
            pipe.Dispose();
        }
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task Watch_IdleNativeRead_CancellationCompletes(bool withCursor, bool beforeIssue)
    {
        var pipe = await IdleUsnPipe.CreateAsync(1);
        try
        {
            FileUtilities._getVolumeHandle = pipe.BorrowHandle;
            using var volume = MftVolume.Open("C");
            using var cancellation = new CancellationTokenSource();
            var watch = ConsumeIdleWatchAsync(volume, withCursor, cancellation.Token);
            try
            {
                await IdleUsnPipe.AwaitSignalAsync(pipe.BeforeIssue);
                if (!beforeIssue)
                {
                    pipe.ContinueIssue.Set();
                    await IdleUsnPipe.AwaitSignalAsync(pipe.Issued);
                }
                await cancellation.CancelAsync();
                pipe.ContinueIssue.Set();
                await watch.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                pipe.UnblockForCleanup();
                await cancellation.CancelAsync();
                try
                {
                    await watch.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException) when (cancellation.IsCancellationRequested)
                {
                    // Closing the test pipe can fault the read during failed-test teardown.
                }
            }
        }
        finally
        {
            FileUtilities.ResetToDefaults();
            pipe.Dispose();
        }
    }

    static async Task ConsumeIdleWatchAsync(MftVolume volume, bool withCursor, CancellationToken token)
    {
        try
        {
            if (withCursor)
            {
                await foreach (var batch in volume.WatchUsnJournalWithCursor(Cursor, token))
                {
                    Assert.Fail($"Unexpected idle batch with {batch.Entries.Length} entries.");
                }
            }
            else
            {
                await foreach (var batch in volume.WatchUsnJournal(Cursor, token))
                {
                    Assert.Fail($"Unexpected idle batch with {batch.Length} entries.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
