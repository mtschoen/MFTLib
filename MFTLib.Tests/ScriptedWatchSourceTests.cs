using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The scripted watch source is a public test double that consumer test assemblies drive, so its
///     scripting surface is pinned here directly, without an index.
/// </summary>
[TestClass]
public class ScriptedWatchSourceTests
{
    static readonly TimeSpan HangGuard = ScriptedWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    static IndexWatchTarget Target(char driveLetter, ulong journalId = 7, long nextUsn = 100) =>
        new(driveLetter, journalId, nextUsn);

    async Task<ScriptedDriveWatch> StartAsync(ScriptedWatchSource source, IndexWatchTarget target) =>
        (ScriptedDriveWatch)await ((IIndexWatchSource)source).StartAsync(target, Token);

    static IAsyncEnumerator<WatchStreamItem> Read(ScriptedDriveWatch watch, CancellationToken cancellationToken) =>
        ((IIndexDriveWatch)watch).ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    [TestMethod]
    public async Task StartAsync_RecordsEachStartAndHandsOutAWatchForTheDrive()
    {
        var source = new ScriptedWatchSource();

        var watch = await StartAsync(source, Target('t', 9, 250));

        Assert.AreEqual(new ScriptedWatchStart('t', new SyntheticJournalCursor(9, 250)), source.Starts.Single());
        Assert.AreEqual(new SyntheticJournalCursor(9, 250), source.Starts.Single().Cursor);
        Assert.AreSame(watch, source.Watches.Single());
        Assert.AreSame(watch, source.WatchFor('T'));
        Assert.AreEqual(new UsnJournalCursor(9, 250), watch.StartCursor);
        Assert.AreEqual('t', watch.DriveLetter);
        Assert.IsFalse(watch.ReadStarted);
    }

    [TestMethod]
    public async Task CatchUpOnStart_QueuesTheCaughtUpMarkerBeforeAnythingElse()
    {
        var source = new ScriptedWatchSource { CatchUpOnStart = true };
        var watch = await StartAsync(source, Target('T'));

        await using var enumerator = Read(watch, Token);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.IsInstanceOfType<DriveCaughtUp>(enumerator.Current);
        Assert.IsTrue(watch.ReadStarted);
    }

    [TestMethod]
    public async Task CatchUpOnStart_DefaultsToOffSoTheWatchYieldsNothingUntilScripted()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        await using var enumerator = Read(watch, Token);

        var pending = enumerator.MoveNextAsync().AsTask();
        Assert.IsFalse(pending.IsCompleted, "nothing is yielded until the test scripts it");

        var published = watch.PublishCaughtUpAsync();

        Assert.IsTrue(await pending.WaitAsync(HangGuard, Token));
        Assert.IsInstanceOfType<DriveCaughtUp>(enumerator.Current);
        var next = enumerator.MoveNextAsync().AsTask();
        await published.WaitAsync(HangGuard, Token);
        watch.End();
        Assert.IsFalse(await next.WaitAsync(HangGuard, Token));
    }

    [TestMethod]
    public async Task StartFailure_ThrowsOnEveryStartItReturnsAnExceptionFor()
    {
        var failure = new IOException("drive U is gone");
        var source = new ScriptedWatchSource { StartFailure = letter => letter == 'U' ? failure : null };

        var first = await Assert.ThrowsExceptionAsync<IOException>(() => StartAsync(source, Target('U')));
        var second = await Assert.ThrowsExceptionAsync<IOException>(() => StartAsync(source, Target('U')));
        await StartAsync(source, Target('T'));

        Assert.AreSame(failure, first);
        Assert.AreSame(failure, second);
        CollectionAssert.AreEqual(new[] { 'U', 'U', 'T' }, source.Starts.Select(start => start.DriveLetter).ToArray(),
            "failed starts are still recorded");
        Assert.AreEqual(1, source.Watches.Count, "only the successful start handed out a watch");
    }

    [TestMethod]
    public async Task FailNextStart_FailsOneStartAndThenRecovers()
    {
        var source = new ScriptedWatchSource();
        source.FailNextStart(new IOException("once"));

        await Assert.ThrowsExceptionAsync<IOException>(() => StartAsync(source, Target('T')));
        var watch = await StartAsync(source, Target('T'));

        Assert.IsNotNull(watch);
        Assert.AreEqual(2, source.Starts.Count);
    }

    [TestMethod]
    public async Task FailNextStartFor_FailsOnlyTheNamedDrive()
    {
        var source = new ScriptedWatchSource();
        source.FailNextStartFor('u', new IOException("U only"));

        await StartAsync(source, Target('T'));
        await Assert.ThrowsExceptionAsync<IOException>(() => StartAsync(source, Target('U')));
        await StartAsync(source, Target('U'));

        Assert.AreEqual(2, source.Watches.Count);
    }

    [TestMethod]
    public async Task WaitForStartAsync_CompletesWithTheWatchHandedOutForTheDriveLater()
    {
        var source = new ScriptedWatchSource();
        var waiting = source.WaitForStartAsync('T', 1, Token);
        await StartAsync(source, Target('U'));

        Assert.IsFalse(waiting.IsCompleted, "a start of another drive does not satisfy the wait");

        var started = await StartAsync(source, Target('T'));

        Assert.AreSame(started, await waiting.WaitAsync(HangGuard, Token));
    }

    [TestMethod]
    public async Task WaitForStartAsync_ACallAfterTheStart_IsSatisfiedAtOnce()
    {
        var source = new ScriptedWatchSource();
        var started = await StartAsync(source, Target('T'));

        var waiting = source.WaitForStartAsync('t', 1, Token);

        Assert.IsTrue(waiting.IsCompleted, "a start that happened before the call must not be missed");
        Assert.AreSame(started, await waiting);
    }

    [TestMethod]
    public async Task WaitForStartAsync_TheNthStartOfADriveIsTheNthWatchHandedOutForIt()
    {
        var source = new ScriptedWatchSource();
        var first = await StartAsync(source, Target('T'));
        await StartAsync(source, Target('U'));
        var secondWait = source.WaitForStartAsync('T', 2, Token);
        var thirdWait = source.WaitForStartAsync('T', 3, Token);
        Assert.AreSame(first, await source.WaitForStartAsync('T', 1, Token));
        Assert.IsFalse(secondWait.IsCompleted, "the drive was started once, not twice");

        await ((IAsyncDisposable)first).DisposeAsync();
        var second = await StartAsync(source, Target('T'));

        Assert.AreSame(second, await secondWait.WaitAsync(HangGuard, Token));
        Assert.IsFalse(thirdWait.IsCompleted);
        Assert.AreSame(second, await source.WaitForStartAsync('T', 2, Token));
        await ((IAsyncDisposable)second).DisposeAsync();
        var third = await StartAsync(source, Target('T'));
        Assert.AreSame(third, await thirdWait.WaitAsync(HangGuard, Token));
    }

    [TestMethod]
    public async Task WaitForStartAsync_AFailedStartHandsOutNoWatchAndIsNotCounted()
    {
        var source = new ScriptedWatchSource();
        source.FailNextStart(new IOException("refused"));
        await Assert.ThrowsExceptionAsync<IOException>(() => StartAsync(source, Target('T')));
        var waiting = source.WaitForStartAsync('T', 1, Token);
        Assert.IsFalse(waiting.IsCompleted);

        var started = await StartAsync(source, Target('T'));

        Assert.AreSame(started, await waiting.WaitAsync(HangGuard, Token));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void WaitForStartAsync_AStartNumberBelowOne_IsRejected(int startNumber)
    {
        var source = new ScriptedWatchSource();

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => source.WaitForStartAsync('T', startNumber, Token));
    }

    [TestMethod]
    public async Task WaitForStartAsync_EndsWithCancellationWhenTheTokenIsCancelled()
    {
        var source = new ScriptedWatchSource();
        using var cancellation = new CancellationTokenSource();
        var waiting = source.WaitForStartAsync('T', 1, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => waiting);
    }

    [TestMethod]
    public async Task StartAsync_RefusesASecondStartWhileTheDrivesWatchIsStillRunning()
    {
        var source = new ScriptedWatchSource();
        var first = await StartAsync(source, Target('T'));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => StartAsync(source, Target('T')));
        await StartAsync(source, Target('U'));
        await ((IAsyncDisposable)first).DisposeAsync();
        var second = await StartAsync(source, Target('T'));

        Assert.AreNotSame(first, second);
        Assert.AreSame(second, source.WatchFor('T'));
    }

    [TestMethod]
    public async Task DisposeAsync_ThrowsOnTheSecondDisposalAndCountsBoth()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));

        await ((IAsyncDisposable)watch).DisposeAsync();
        await watch.Disposed.WaitAsync(HangGuard, Token);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await ((IAsyncDisposable)watch).DisposeAsync());
        Assert.AreEqual(2, watch.DisposeCount);
    }

    [TestMethod]
    public async Task PublishBatchAsync_CompletesOnlyOnceThePumpAsksForTheNextItem()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        await using var enumerator = Read(watch, Token);
        var entry = JournalEntries.CreateSynthetic(20, 150, "a.txt");

        var published = watch.PublishBatchAsync([entry], new SyntheticJournalCursor(7, 200));
        Assert.IsTrue(await enumerator.MoveNextAsync());

        var batch = (JournalBatch)enumerator.Current;
        Assert.AreSame(entry.FileName, batch.Entries.Single().FileName);
        Assert.AreEqual(7UL, batch.JournalId);
        Assert.AreEqual(200L, batch.NextUsn);
        Assert.IsFalse(published.IsCompleted, "the pump has not finished with the batch yet");

        var next = enumerator.MoveNextAsync().AsTask();
        await published.WaitAsync(HangGuard, Token);

        var queuedCaughtUp = watch.QueueCaughtUp();
        Assert.IsTrue(await next.WaitAsync(HangGuard, Token));
        Assert.IsInstanceOfType<DriveCaughtUp>(enumerator.Current);
        Assert.IsFalse(queuedCaughtUp.IsCompleted);
    }

    [DataTestMethod]
    [DataRow("open", false)]
    [DataRow("end", false)]
    [DataRow("fail-drive", false)]
    [DataRow("lose-channel", false)]
    [DataRow("dispose", false)]
    [DataRow("end", true)]
    [DataRow("fail-drive", true)]
    [DataRow("lose-channel", true)]
    [DataRow("dispose", true)]
    [DataRow("cancel-pending", false)]
    [DataRow("cancel-pending", true)]
    [DataRow("cancel-suspended", false)]
    [DataRow("cancel-suspended", true)]
    [DataRow("fail-on-cancellation", false)]
    [DataRow("fail-on-cancellation", true)]
    [DataRow("abandon", false)]
    [DataRow("abandon", true)]
    public async Task QueueBatch_ReturnsWithoutWaitingForThePump(string closeMode, bool isMarker)
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));

        switch (closeMode)
        {
            case "open":
                break;
            case "end":
                watch.End();
                break;
            case "fail-drive":
                watch.FailDrive(new IOException("drive failed"));
                break;
            case "lose-channel":
                watch.LoseChannel(new IOException("channel lost"));
                break;
            case "dispose":
                await ((IAsyncDisposable)watch).DisposeAsync();
                break;
            default:
                await EndReadAsync(watch, closeMode);
                Assert.AreEqual(0, watch.DisposeCount, "the read ended without the watch being disposed");
                break;
        }

        var entry = JournalEntries.CreateSynthetic(21, 250, "b.txt");
        var cursor = new SyntheticJournalCursor(7, 300);

        if (closeMode == "open")
        {
            var queued = watch.QueueBatch([entry], cursor);
            Assert.IsFalse(queued.IsCompleted);
            return;
        }

        if (isMarker)
        {
            Assert.ThrowsException<InvalidOperationException>(() => watch.QueueCaughtUp());
            Assert.ThrowsException<InvalidOperationException>(() => watch.PublishCaughtUpAsync());
        }
        else
        {
            Assert.ThrowsException<InvalidOperationException>(() => watch.QueueBatch([entry], cursor));
            Assert.ThrowsException<InvalidOperationException>(() => watch.PublishBatchAsync([entry], cursor));
        }
    }

    [DataTestMethod]
    [DataRow("cancel", false)]
    [DataRow("cancel", true)]
    [DataRow("fail-on-cancellation", false)]
    [DataRow("fail-on-cancellation", true)]
    [DataRow("abandon", false)]
    [DataRow("abandon", true)]
    [DataRow("fault", false)]
    [DataRow("fault", true)]
    public async Task Teardown_SettlesUnreadReceipts(string teardownMode, bool unreadIsMarker)
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        using var cancellation = new CancellationTokenSource();
        var enumerator = Read(watch, cancellation.Token);

        var batchEntry = JournalEntries.CreateSynthetic(21, 250, "b.txt");
        var batchCursor = new SyntheticJournalCursor(7, 300);

        Task yieldedTask;
        Task unreadTask;
        if (unreadIsMarker)
        {
            yieldedTask = watch.QueueBatch([batchEntry], batchCursor);
            unreadTask = watch.QueueCaughtUp();
        }
        else
        {
            yieldedTask = watch.QueueCaughtUp();
            unreadTask = watch.QueueBatch([batchEntry], batchCursor);
        }

        Assert.IsTrue(await enumerator.MoveNextAsync());
        var fault = new IOException("channel lost");
        switch (teardownMode)
        {
            case "fault":
                watch.LoseChannel(fault);
                break;
            case "fail-on-cancellation":
                watch.FailOnCancellation(new IOException("pipe closed under the read"));
                await cancellation.CancelAsync();
                break;
            case "cancel":
                await cancellation.CancelAsync();
                break;
        }

        await enumerator.DisposeAsync();
        await watch.ReadEnded.WaitAsync(HangGuard, Token);

        Assert.AreEqual(0, watch.DisposeCount, "the read ended without the watch being disposed");
        Assert.IsTrue(yieldedTask.IsCompletedSuccessfully, "the yielded item completes successfully");
        if (teardownMode == "fault")
        {
            Assert.IsTrue(unreadTask.IsFaulted, "the unread item is settled as faulted");
            Assert.AreSame(fault, unreadTask.Exception?.InnerException);
        }
        else
        {
            Assert.IsTrue(unreadTask.IsCanceled, "the unread item is settled as cancelled");
        }

        AssertDeliveryRejected(watch);
        await ((IAsyncDisposable)watch).DisposeAsync();
        Assert.IsTrue(yieldedTask.IsCompletedSuccessfully, "disposal does not settle a receipt again");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Teardown_SettlesEveryReceiptWhenDisposalOverlapsTheReader(bool overlapReaderDrain)
    {
        const int markerCount = 10_000;
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        using var cancellation = new CancellationTokenSource();
        var enumerator = Read(watch, cancellation.Token);
        var receipts = Enumerable.Range(0, markerCount).Select(_ => watch.QueueCaughtUp()).ToArray();
        Assert.IsTrue(await enumerator.MoveNextAsync());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reader = EndReadOnReleaseAsync(release.Task, enumerator, overlapReaderDrain ? cancellation : null);
        var disposer = DisposeOnReleaseAsync(release.Task, watch);
        release.SetResult();

        await reader.WaitAsync(HangGuard, Token);
        await watch.ReadEnded.WaitAsync(HangGuard, Token);
        Assert.AreEqual(markerCount, receipts.Count(receipt => receipt.IsCompleted),
            "every accepted receipt settles before the read ends");
        await disposer.WaitAsync(HangGuard, Token);
        Assert.IsTrue(receipts[0].IsCompletedSuccessfully, "the item the pump took completes successfully");
        Assert.IsTrue(receipts.All(receipt => receipt.IsCompletedSuccessfully || receipt.IsCanceled),
            "an unread receipt settles as cancelled, never as a fault");
        AssertDeliveryRejected(watch);
    }

    [TestMethod]
    public async Task ReadEnded_WaitsForADisposalDrainThatDequeuedAnUnreadItem()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        var enumerator = Read(watch, Token);
        var yielded = watch.QueueCaughtUp();
        var unread = watch.QueueCaughtUp();
        Assert.IsTrue(await enumerator.MoveNextAsync());
        var gate = new TestGate();
        watch.BeforeSettleUnread = () =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
        };

        var disposer = Task.Run(() => ((IAsyncDisposable)watch).DisposeAsync().AsTask(), Token);
        try
        {
            await gate.Entered.WaitAsync(HangGuard, Token);
            await enumerator.DisposeAsync();

            Assert.IsTrue(yielded.IsCompletedSuccessfully, "the yielded item completes when the reader moves on");
            Assert.IsFalse(unread.IsCompleted, "disposal holds the unread item it dequeued");
            Assert.IsFalse(watch.ReadEnded.IsCompleted, "the read has not ended while a dequeued item is unsettled");
        }
        finally
        {
            gate.Release();
        }

        await disposer.WaitAsync(HangGuard, Token);
        Assert.IsFalse(await watch.ReadEnded.WaitAsync(HangGuard, Token));
        Assert.IsTrue(unread.IsCanceled, "the unread item settles as cancelled");
    }

    static async Task EndReadOnReleaseAsync(Task release, IAsyncEnumerator<WatchStreamItem> enumerator,
        CancellationTokenSource? cancelInsteadOfReading)
    {
        await release;
        if (cancelInsteadOfReading is not null)
        {
            await cancelInsteadOfReading.CancelAsync();
        }
        else
        {
            while (await enumerator.MoveNextAsync())
            {
            }
        }

        await enumerator.DisposeAsync();
    }

    static async Task DisposeOnReleaseAsync(Task release, IAsyncDisposable watch)
    {
        await release;
        await watch.DisposeAsync();
    }

    static void AssertDeliveryRejected(ScriptedDriveWatch watch)
    {
        var entry = JournalEntries.CreateSynthetic(22, 260, "c.txt");
        var cursor = new SyntheticJournalCursor(7, 400);
        Assert.ThrowsException<InvalidOperationException>(() => watch.QueueBatch([entry], cursor));
        Assert.ThrowsException<InvalidOperationException>(() => watch.PublishBatchAsync([entry], cursor));
        Assert.ThrowsException<InvalidOperationException>(() => watch.QueueCaughtUp());
        Assert.ThrowsException<InvalidOperationException>(() => watch.PublishCaughtUpAsync());
    }

    async Task EndReadAsync(ScriptedDriveWatch watch, string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var enumerator = Read(watch, cancellation.Token);
        switch (mode)
        {
            case "cancel-pending":
                {
                    var pending = enumerator.MoveNextAsync().AsTask();
                    await cancellation.CancelAsync();
                    await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
                    break;
                }
            case "fail-on-cancellation":
                {
                    watch.FailOnCancellation(new IOException("pipe closed under the read"));
                    var pending = enumerator.MoveNextAsync().AsTask();
                    await cancellation.CancelAsync();
                    await Assert.ThrowsExceptionAsync<IOException>(() => pending);
                    break;
                }
            case "cancel-suspended":
                _ = watch.QueueCaughtUp();
                Assert.IsTrue(await enumerator.MoveNextAsync());
                await cancellation.CancelAsync();
                break;
            case "abandon":
                _ = watch.QueueCaughtUp();
                Assert.IsTrue(await enumerator.MoveNextAsync());
                break;
        }

        await enumerator.DisposeAsync();
        await watch.ReadEnded.WaitAsync(HangGuard, Token);
    }

    [TestMethod]
    public async Task ReadEnded_ReportsFalseWhenTheReadEndsNormally()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        await using var enumerator = Read(watch, Token);

        watch.End();

        Assert.IsFalse(await enumerator.MoveNextAsync());
        Assert.IsFalse(await watch.ReadEnded.WaitAsync(HangGuard, Token));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadEnded_ReportsTrueWhenCancellationEndedTheRead(bool disposeWhileSuspended)
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        using var cancellation = new CancellationTokenSource();
        var enumerator = Read(watch, cancellation.Token);

        if (disposeWhileSuspended)
        {
            _ = watch.QueueCaughtUp();
            Assert.IsTrue(await enumerator.MoveNextAsync());
            await cancellation.CancelAsync();
            await enumerator.DisposeAsync();
        }
        else
        {
            var pending = enumerator.MoveNextAsync().AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
            await enumerator.DisposeAsync();
        }

        Assert.IsTrue(await watch.ReadEnded.WaitAsync(HangGuard, Token));
    }

    [TestMethod]
    public async Task FailOnCancellation_ReplacesTheCancellationWithTheScriptedFailure()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        var failure = new IOException("pipe closed under the read");
        watch.FailOnCancellation(failure);
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = Read(watch, cancellation.Token);
        var pending = enumerator.MoveNextAsync().AsTask();

        await cancellation.CancelAsync();

        Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<IOException>(() => pending));
        Assert.IsTrue(await watch.ReadEnded.WaitAsync(HangGuard, Token));
    }

    [TestMethod]
    public async Task FailDrive_MakesTheReadThrowADriveWatchFaultNamingTheDrive()
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        await using var enumerator = Read(watch, Token);
        var inner = new InvalidOperationException("journal deleted");

        watch.FailDrive(inner);

        var fault = await Assert.ThrowsExceptionAsync<DriveWatchFaultException>(async () => await enumerator.MoveNextAsync());
        Assert.AreEqual('T', fault.DriveLetter);
        Assert.AreSame(inner, fault.InnerException);
        Assert.IsFalse(await watch.ReadEnded.WaitAsync(HangGuard, Token));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoseChannel_MakesTheReadThrowTheScriptedException(bool isCanceledException)
    {
        var source = new ScriptedWatchSource();
        var watch = await StartAsync(source, Target('T'));
        if (isCanceledException)
        {
            watch.FailOnCancellation(new InvalidOperationException("armed cancellation failure"));
        }

        await using var enumerator = Read(watch, Token);
        Exception failure = isCanceledException
            ? new OperationCanceledException("channel cancelled externally")
            : new IOException("pipe broken");

        watch.LoseChannel(failure);

        Exception thrown = isCanceledException
            ? await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync())
            : await Assert.ThrowsExceptionAsync<IOException>(async () => await enumerator.MoveNextAsync());
        Assert.AreSame(failure, thrown);
        if (isCanceledException)
        {
            Assert.IsFalse(await watch.ReadEnded.WaitAsync(HangGuard, Token));
        }
    }

    [TestMethod]
    public void WatchFor_ThrowsWhenNoWatchWasHandedOutForTheDrive()
    {
        var source = new ScriptedWatchSource();

        Assert.ThrowsException<InvalidOperationException>(() => source.WatchFor('T'));
    }

    [TestMethod]
    public void TestGate_WaitSynchronously_TimesOutWhenTaskDoesNotComplete()
    {
        var pending = new TaskCompletionSource().Task;
        Assert.ThrowsException<TimeoutException>(() => TestGate.WaitSynchronously(pending, TimeSpan.Zero));
        TestGate.WaitSynchronously(Task.CompletedTask, TimeSpan.Zero);
    }
}
