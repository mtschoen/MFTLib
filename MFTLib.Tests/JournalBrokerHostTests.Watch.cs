using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_StreamsBatches_UntilCancelled()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new[]
        {
            (new[] { WatchEntry() }, new UsnJournalCursor(7UL, 110L)),
            ([WatchEntry()], new UsnJournalCursor(7UL, 120L))
        };
        var host = CreateWatchHost(
            watchDrive: (_, _, _, cancellationToken) =>
            {
                cancellationToken.Register(() => cancelled.TrySetResult());
                return LiveWatch(batches, cancellationToken);
            });
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var first = await HostChannelHarness.ReadFrameAsync(pipe);
        var second = await HostChannelHarness.ReadFrameAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, first?.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, second?.Kind);
        Assert.AreEqual(new UsnJournalCursor(7UL, 110L), first?.Cursor);
        Assert.AreEqual(new UsnJournalCursor(7UL, 120L), second?.Cursor);
        Assert.IsFalse(cancelled.Task.IsCompleted, "The watch stays open while its pipe is open.");

        await pipe.DisposeAsync();
        await cancelled.Task.WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursor_QueriesCurrentCursorBeforeWatching()
    {
        UsnJournalCursor watchedFrom = default;
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(9UL, 500L),
            watchDrive: (_, since, _, _) =>
            {
                watchedFrom = since;
                return FiniteWatch([([WatchEntry()], new UsnJournalCursor(9UL, 510L))]);
            });
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', default);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.CaughtUp, BrokerFrameKind.JournalBatch },
            frames.Select(frame => frame.Kind).ToArray());
        Assert.AreEqual(new UsnJournalCursor(9UL, 500L), watchedFrom);
    }

    [TestMethod]
    public async Task StartWatch_NoWatchSourceConfigured_EmitsErrorFrame()
    {
        var host = CreateWatchHost();
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual("Broker has no watch source", frames[0].Message);
    }

    [TestMethod]
    public async Task WatchChannel_SourceCompletesNaturally_ClosesWithoutError()
    {
        var host = CreateWatchHost(
            watchDrive: (_, _, _, _) => FiniteWatch([([WatchEntry()], new UsnJournalCursor(7UL, 110L))]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.JournalBatch }, frames.Select(frame => frame.Kind).ToArray(),
            "Natural completion is not a fault: no Error frame is sent before the channel closes.");
        await AssertControlStillServesAsync(harness);
    }

    [TestMethod]
    public async Task WatchChannel_BatchWriteHitsBrokenPipe_EndsQuietly()
    {
        var secondBatchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = new UsnJournalCursor(7UL, 100L);
        var host = CreateWatchHost(
            queryCursor: _ => armed,
            watchDrive: (drive, _, _, cancellationToken) => GatedWatch(
                ([WatchEntry()], new UsnJournalCursor(7UL, 110L)),
                drive == "C" ? secondBatchGate.Task : siblingGate.Task,
                ([WatchEntry()], new UsnJournalCursor(7UL, 120L)),
                cancellationToken));
        var drivePipe = new BreakableDrivePipe('C');
        await using var harness = new HostChannelHarness(host, connectChannel: drivePipe.ConnectAsync);
        drivePipe.Harness = harness;

        // The watch is live over the healthy pipe: the leading CaughtUp (the armed cursor equals
        // the journal tip) and one batch arrive on each channel.
        var pipe = await harness.OpenWatchChannelAsync('C', armed);
        var sibling = await harness.OpenWatchChannelAsync('D', armed);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(sibling))?.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, (await HostChannelHarness.ReadFrameAsync(sibling))?.Kind);

        // The client goes away while its watch is still armed. Breaking the pipe and only then
        // releasing the pending batch keeps the disconnect deterministic.
        var brokenHostEnd = await drivePipe.Connected.WaitAsync(HostChannelHarness.HangGuard);
        brokenHostEnd.BreakPipe();
        secondBatchGate.SetResult();
        await brokenHostEnd.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        await AssertOnlyThatChannelEndedAsync(harness, pipe, sibling, siblingGate);
    }

    [TestMethod]
    public async Task WatchChannel_FaultWithBrokenPipe_ErrorFrameUnsendable_EndsQuietly()
    {
        var faultGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = new UsnJournalCursor(7UL, 100L);
        var host = CreateWatchHost(
            queryCursor: _ => armed,
            watchDrive: (drive, _, _, cancellationToken) => drive == "C"
                ? FaultingAfterGate(([WatchEntry()], new UsnJournalCursor(7UL, 110L)), faultGate.Task,
                    cancellationToken)
                : GatedWatch(([WatchEntry()], new UsnJournalCursor(7UL, 110L)), siblingGate.Task,
                    ([WatchEntry()], new UsnJournalCursor(7UL, 120L)), cancellationToken));
        var drivePipe = new BreakableDrivePipe('C');
        await using var harness = new HostChannelHarness(host, connectChannel: drivePipe.ConnectAsync);
        drivePipe.Harness = harness;

        var pipe = await harness.OpenWatchChannelAsync('C', armed);
        var sibling = await harness.OpenWatchChannelAsync('D', armed);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(sibling))?.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, (await HostChannelHarness.ReadFrameAsync(sibling))?.Kind);

        // The pipe dies, then the watch faults for real: the Error frame that reports the fault
        // has no one left to receive it, and its write fails too.
        var brokenHostEnd = await drivePipe.Connected.WaitAsync(HostChannelHarness.HangGuard);
        brokenHostEnd.BreakPipe();
        faultGate.SetResult();
        await brokenHostEnd.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        await AssertOnlyThatChannelEndedAsync(harness, pipe, sibling, siblingGate);
    }

    [TestMethod]
    public async Task WatchChannel_LeadingCaughtUpWriteHitsBrokenPipe_EndsQuietly()
    {
        var siblingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = new UsnJournalCursor(7UL, 100L);
        var host = CreateWatchHost(
            queryCursor: _ => armed,
            watchDrive: (drive, _, _, cancellationToken) => drive == "C"
                ? LiveWatch([([WatchEntry()], new UsnJournalCursor(7UL, 110L))], cancellationToken)
                : GatedWatch(([WatchEntry()], new UsnJournalCursor(7UL, 110L)), siblingGate.Task,
                    ([WatchEntry()], new UsnJournalCursor(7UL, 120L)), cancellationToken));
        // The pipe is already gone by the time the watch arms, so the leading CaughtUp for a
        // cursor that sits at the journal tip is the first frame to land on the dead client.
        var drivePipe = new BreakableDrivePipe('C', brokenOnConnect: true);
        await using var harness = new HostChannelHarness(host, connectChannel: drivePipe.ConnectAsync);
        drivePipe.Harness = harness;

        var sibling = await harness.OpenWatchChannelAsync('D', armed);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, (await HostChannelHarness.ReadFrameAsync(sibling))?.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, (await HostChannelHarness.ReadFrameAsync(sibling))?.Kind);
        var pipe = await harness.OpenWatchChannelAsync('C', armed);
        var brokenHostEnd = await drivePipe.Connected.WaitAsync(HostChannelHarness.HangGuard);
        await brokenHostEnd.WriteFailureObserved.WaitAsync(HostChannelHarness.HangGuard);

        await AssertOnlyThatChannelEndedAsync(harness, pipe, sibling, siblingGate);
    }

    // The failed channel ended without ending the session: a sibling channel still delivers a batch
    // released after the failure, and the control pipe still answers.
    static async Task AssertOnlyThatChannelEndedAsync(HostChannelHarness harness, Stream failed, Stream sibling,
        TaskCompletionSource siblingGate)
    {
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(failed), "The host closes the failed channel.");
        siblingGate.SetResult();
        var batch = await HostChannelHarness.ReadFrameAsync(sibling);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch?.Kind);
        Assert.AreEqual(120L, batch?.Cursor.NextUsn);
        await AssertControlStillServesAsync(harness);
    }
}
