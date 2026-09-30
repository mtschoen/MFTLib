using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The host's arm query consults JournalCheckpointCheck, whose override other classes install.
[TestClass]
[DoNotParallelize]
public class BrokerIndexWatchSourceTests
{
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    [TestMethod]
    public async Task WatchSource_StartsOnePipePerDriveAndEachBatchArrivesOnItsOwnHandle()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var token = harness.CancellationToken;
        await using var handleC = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        await using var handleD = await source.StartAsync(new IndexWatchTarget('D', 7, 200), token);
        var readerC = handleC.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = readerC.ConfigureAwait(false);
        var readerD = handleD.ReadAsync(token).GetAsyncEnumerator(token);
        await using var __ = readerD.ConfigureAwait(false);

        var runC = await harness.Watch('C').RunAsync(1);
        var runD = await harness.Watch('D').RunAsync(1);
        Assert.AreEqual(new UsnJournalCursor(7, 100), runC.Since);
        Assert.AreEqual(new UsnJournalCursor(7, 200), runD.Since);
        Assert.AreEqual('C', handleC.DriveLetter);
        Assert.AreEqual('D', handleD.DriveLetter);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerC));
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerD));
        runC.Push(1, "c.txt", 110);
        runD.Push(2, "d.txt", 210);

        var batchC = await WatchReads.NextBatchAsync(readerC);
        var batchD = await WatchReads.NextBatchAsync(readerD);
        Assert.AreEqual(7ul, batchC.JournalId);
        Assert.AreEqual(110L, batchC.NextUsn);
        Assert.AreEqual("c.txt", batchC.Entries.Single().FileName);
        Assert.AreEqual(7ul, batchD.JournalId);
        Assert.AreEqual(210L, batchD.NextUsn);
        Assert.AreEqual("d.txt", batchD.Entries.Single().FileName);
        Assert.AreEqual(2, harness.ConnectionCount, "each start connects once and shares the process");
    }

    [TestMethod]
    public async Task WatchSource_LeavesTheBorrowedProcessReadyForAnotherWatch()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var token = harness.CancellationToken;
        var first = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var firstReader = first.ReadAsync(token).GetAsyncEnumerator(token);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(firstReader));
        var firstRun = await harness.Watch('C').RunAsync(1);
        firstRun.Push(1, "before.txt", 110);
        await WatchReads.NextBatchAsync(firstReader);

        await firstReader.DisposeAsync();
        await first.DisposeAsync();
        await firstRun.Cancelled.WaitAsync(HangGuard);

        // The process is the caller's: closing one drive's watch leaves it serving the next.
        await using var second = await source.StartAsync(new IndexWatchTarget('C', 7, 110), token);
        var secondReader = second.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = secondReader.ConfigureAwait(false);
        var secondRun = await harness.Watch('C').RunAsync(2);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(secondReader));
        secondRun.Push(2, "after.txt", 120);
        Assert.AreEqual("after.txt", (await WatchReads.NextBatchAsync(secondReader)).Entries.Single().FileName);
        Assert.IsFalse(harness.Process.HasEnded);
    }

    [TestMethod]
    public async Task WatchSource_CompletesWhenTheTokenIsCancelled()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        await using var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 100), harness.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(harness.CancellationToken);
        var reader = handle.ReadAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        await using var _ = reader.ConfigureAwait(false);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
        var pending = reader.MoveNextAsync().AsTask();

        await cancellation.CancelAsync();

        await WatchReads.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(HangGuard));
    }

    [TestMethod]
    public async Task WatchSource_CancellationLeavesBorrowedProcessReadyForRestart()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var token = harness.CancellationToken;
        var cancelled = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelledReader = cancelled.ReadAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(cancelledReader));
        var pending = cancelledReader.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();
        await WatchReads.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(HangGuard));
        await cancelledReader.DisposeAsync();
        await cancelled.DisposeAsync();

        await using var restarted = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var reader = restarted.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = reader.ConfigureAwait(false);
        var run = await harness.Watch('C').RunAsync(2);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
        run.Push(1, "after-restart.txt", 110);

        var batch = await WatchReads.NextBatchAsync(reader);
        Assert.AreEqual("after-restart.txt", batch.Entries.Single().FileName);
        Assert.AreEqual(110L, batch.NextUsn);
    }

    [TestMethod]
    public async Task WatchSource_OneDrivesErrorDoesNotEndAnotherDrivesHandle()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var token = harness.CancellationToken;
        await using var handleC = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        await using var handleD = await source.StartAsync(new IndexWatchTarget('D', 7, 100), token);
        var readerC = handleC.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = readerC.ConfigureAwait(false);
        var readerD = handleD.ReadAsync(token).GetAsyncEnumerator(token);
        await using var __ = readerD.ConfigureAwait(false);
        var runC = await harness.Watch('C').RunAsync(1);
        var runD = await harness.Watch('D').RunAsync(1);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerC));
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(readerD));

        runD.Fail(new IOException("journal wrapped"));
        runC.Push(1, "c.txt", 110);

        await WatchReads.ThrowsNextAsync<DriveWatchFaultException>(readerD);
        Assert.AreEqual(110L, (await WatchReads.NextBatchAsync(readerC)).NextUsn);
    }

    [TestMethod]
    public async Task WatchSource_HostClosingThePipeIsAChannelLossNotAnEnd()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        await using var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 100), harness.CancellationToken);
        var reader = handle.ReadAsync(harness.CancellationToken).GetAsyncEnumerator(harness.CancellationToken);
        await using var _ = reader.ConfigureAwait(false);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));

        (await harness.Watch('C').RunAsync(1)).End();

        var lost = await WatchReads.ThrowsNextAsync<BrokerChannelLostException>(reader);
        Assert.AreEqual('C', lost.DriveLetter);
    }

    [TestMethod]
    public async Task WatchSource_DoesNotConnectWhenTokenIsAlreadyCancelled()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

        await WatchReads.ThrowsAsync<OperationCanceledException>(() =>
            source.StartAsync(new IndexWatchTarget('C', 7, 100), cancellation.Token));

        Assert.AreEqual(0, harness.ConnectionCount);
        Assert.AreEqual(0, harness.Watch('C').StartedCount);
    }

    [TestMethod]
    public async Task WatchSource_RejectsANullTargetBeforeConnecting()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            source.StartAsync(null!, harness.CancellationToken));

        Assert.AreEqual(0, harness.ConnectionCount);
    }

    [TestMethod]
    public async Task ReadAsync_TokenCancelled_ReturnsPromptlyWithoutDisposal()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var token = harness.CancellationToken;
        await using var handle = await source.StartAsync(new IndexWatchTarget('C', 7, 100), token);
        var run = await harness.Watch('C').RunAsync(1);
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reader = handle.ReadAsync(pumpStop.Token).GetAsyncEnumerator(pumpStop.Token);
        Assert.IsInstanceOfType<DriveCaughtUp>(await WatchReads.NextAsync(reader));
        var pending = reader.MoveNextAsync().AsTask();

        // The index's pump cancels its read first and disposes the handle only afterwards.
        await pumpStop.CancelAsync();
        await WatchReads.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(HangGuard));
        await reader.DisposeAsync();

        Assert.IsFalse(run.Cancelled.IsCompleted, "cancelling a read must leave the drive's pipe open");
        var reread = handle.ReadAsync(token).GetAsyncEnumerator(token);
        await using var _ = reread.ConfigureAwait(false);
        run.Push(1, "after-cancel.txt", 110);
        Assert.AreEqual("after-cancel.txt", (await WatchReads.NextBatchAsync(reread)).Entries.Single().FileName);
    }

    [TestMethod]
    public async Task TimedOutStop_ThenNewWatch_RunsUndisturbed()
    {
        await using var broker = new ScriptedWatchBrokerHarness();
        var token = broker.CancellationToken;
        var inner = new BrokerMftBlockProducer(broker.ConnectAsync).CreateWatchSource();
        var teardown = new TestGate();
        using var harness = new WatchHarness(new HoldFirstDisposalSource(inner, teardown), 'T');
        var index = harness.Index;
        broker.Watch('T').IgnoreCancellationInNextRun();
        await index.StartWatchingAsync('T', token);
        var first = await broker.Watch('T').RunAsync(1);
        await first.Entered.WaitAsync(HangGuard);

        // The first watch's teardown is held at the handle's disposal, so the stop's wait for it
        // really runs out: the token is cancelled once the stop is waiting on that teardown.
        using var stopTimeout = new CancellationTokenSource();
        var stop = index.StopWatchingAsync('T', stopTimeout.Token);
        await teardown.Entered.WaitAsync(HangGuard);
        Assert.IsFalse(stop.IsCompleted, "the stop waits for the held teardown");
        await stopTimeout.CancelAsync();
        await WatchReads.ThrowsAsync<OperationCanceledException>(() => stop.WaitAsync(HangGuard));
        Assert.IsFalse(first.Cancelled.IsCompleted, "the held teardown has not closed the pipe yet");

        // The teardown carries on after the stop gave up, and closes the pipe.
        teardown.Release();
        await first.Cancelled.WaitAsync(HangGuard);

        await index.StartWatchingAsync('T', token);
        var second = await broker.Watch('T').RunAsync(2);
        var freshApplied = ChangeSignal.WhenApplied(index, "fresh.txt");
        second.Push(9, "fresh.txt", 300);
        await freshApplied;

        // The first host watch is still alive and now yields what it held; its pipe is gone.
        first.Push(10, "stale.txt", 400);
        first.Release();
        await first.Finished.WaitAsync(HangGuard);
        var afterApplied = ChangeSignal.WhenApplied(index, "after.txt");
        second.Push(11, "after.txt", 500);
        await afterApplied;

        CollectionAssert.AreEqual(new[] { "fresh.txt", "after.txt" },
            harness.Changes.Select(change => change.Entry.Name).ToArray());
        Assert.AreEqual(0, harness.Faults.Count, string.Join("; ", harness.Faults.Select(fault => fault.Exception.Message)));
        Assert.AreEqual(500L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.IsFalse(broker.Process.HasEnded);
    }

    // Hands out the inner source's handles, and holds the disposal of the first one on a gate.
    sealed class HoldFirstDisposalSource(IIndexWatchSource inner, TestGate teardown) : IIndexWatchSource
    {
        int _starts;

        public async Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
        {
            var handle = await inner.StartAsync(target, cancellationToken);
            return Interlocked.Increment(ref _starts) == 1 ? new HeldDisposalWatch(handle, teardown) : handle;
        }
    }

    sealed class HeldDisposalWatch(IIndexDriveWatch inner, TestGate teardown) : IIndexDriveWatch
    {
        public char DriveLetter => inner.DriveLetter;

        public IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken) =>
            inner.ReadAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            teardown.MarkEntered();
            await teardown.WaitForReleaseAsync(CancellationToken.None);
            await inner.DisposeAsync();
        }
    }
}
