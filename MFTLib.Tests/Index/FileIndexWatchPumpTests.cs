using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchPumpTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public async Task StartWatchingAsync_EachDriveStartsFromItsOwnHeaderCursor()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.SetNextProducedCursor('T', journalIdentifier: 11, nextUsn: 4242);
        harness.SetNextProducedCursor('U', journalIdentifier: 22, nextUsn: 8484);
        await harness.IndexFor('T').RescanAsync(Token);
        await harness.IndexFor('U').RescanAsync(Token);

        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);

        Assert.AreSequenceEqual(
            [new IndexWatchTarget(new VolumeKey('T'), 11, 4242), new IndexWatchTarget(new VolumeKey('U'), 22, 8484)], [.. harness.Source.Targets]);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_SurfacesASubscriberFaultAndKeepsPumpingUntilThen()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));

        var fault = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreEqual("subscriber failed", fault.Message);
        Assert.AreEqual(2, delivered);
    }

    [TestMethod]
    public async Task WatchFaulted_AnnouncesTheFirstSubscriberFaultImmediatelyAndOnlyOnce()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));

        var faults = harness.Faults;
        Assert.HasCount(1, faults);
        Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);
        Assert.AreEqual('T', faults[0].Drive);
        Assert.AreEqual("subscriber failed", faults[0].Exception.Message);

        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        Assert.HasCount(1, harness.Faults);

        await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task StartWatchingAsync_ReportsAnIndependentSourceOperationCanceledExceptionThroughTheStart()
    {
        using var harness = new WatchHarness();
        var sourceFault = new OperationCanceledException("source aborted");
        harness.Source.FailNextStart(sourceFault);

        // The source throws before it returns a handle, so the start itself reports the failure,
        // as the source's own exception rather than as a cancellation of this call.
        var failure = await DriveResultAssertions.FailureAsync<OperationCanceledException>(
            () => harness.IndexFor('T').StartWatchingAsync(Token));

        Assert.AreSame(sourceFault, failure);
        Assert.IsEmpty(harness.Faults, "reported once, through the start");
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual("source aborted", harness.DriveFor('T').Watch.FailureMessage);

        // The failed start leaves the watch requested with no instance; stop clears the faulted state.
        await harness.IndexFor('T').StopWatchingAsync(Token);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task WatchFaulted_DoesNotFireForAnOrdinaryStop()
    {
        using var harness = new WatchHarness();

        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await harness.IndexFor('T').StopWatchingAsync(Token);

        Assert.IsEmpty(harness.Faults);
    }

    [TestMethod]
    public async Task WatchFaulted_SwallowsAThrowingFaultHandlerAndStopReportsTheOriginalFailure()
    {
        using var harness = new WatchHarness();
        harness.Index.WatchFaulted += _ => throw new NotSupportedException("fault handler failed");
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));

        var fault = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));
        Assert.AreEqual("subscriber failed", fault.Message);
    }

    [TestMethod]
    public async Task SubscriberOperationCanceledException_IsReportedByStopAfterPumpingContinues()
    {
        using var harness = new WatchHarness();
        var subscriberFault = new OperationCanceledException("subscriber cancelled");
        harness.Index.Changed += _ => throw subscriberFault;
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.IndexFor('T').StartWatchingAsync(Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        Assert.HasCount(1, harness.Faults);
        Assert.AreEqual(WatchFaultKind.Subscriber, harness.Faults[0].Kind);

        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        var fault = await DriveResultAssertions.FailureAsync<OperationCanceledException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));

        Assert.AreSame(subscriberFault, fault);
        Assert.AreEqual(2, delivered);
        Assert.HasCount(1, harness.Faults);
    }

    [TestMethod]
    public async Task StopWatchingAsync_EndsAPumpWaitingForAHeldWriteGateWithoutApplyingItsBatch()
    {
        using var harness = new WatchHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.Hooks.ApplyJournalEntriesEntered = _ => entered.TrySetResult();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('T').WaitForDriveWriteGateForTest(new VolumeKey('T'), Token);
        try
        {
            _ = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(recordNumber: 9, "held.txt"));
            await entered.Task.WaitAsync(HangGuard, Token);

            // The pump now waits for the gate this test holds. A stop must end that wait rather
            // than leave a thread parked on the gate until the holder lets go.
            var stopped = await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, Token);
            Assert.AreEqual(DriveOperationOutcome.Succeeded, stopped.Outcome);
        }
        finally
        {
            harness.IndexFor('T').ReleaseDriveWriteGateForTest(new VolumeKey('T'));
        }

        Assert.IsEmpty(harness.Changes, "a batch still waiting for the gate when the stop arrived was applied");
        Assert.IsEmpty(harness.Faults, "a stop during the gate wait was reported as a fault");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisposeAsync_StopsTheWatchBeforeReleasingBlocks(bool holdCacheFile)
    {
        if (holdCacheFile && !OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows sharing-violation cleanup case.");
        }

        var harness = new WatchHarness();
        try
        {
            var applying = harness.TrackGate();
            harness.Index.Changed += _ =>
            {
                applying.MarkEntered();
                applying.WaitForRelease();
            };
            await harness.IndexFor('T').StartWatchingAsync(Token);
            var handle = harness.Source.WatchFor('T');
            _ = handle.Queue(WatchHarness.Batch(recordNumber: 9, "one.txt"));
            await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            var producedBlock = harness.BlockFor('T');

            var dispose = harness.Index.DisposeAsync().AsTask();

            Assert.IsFalse(dispose.IsCompleted, "disposal waits for the pump that is still applying");
            Assert.AreEqual(WatchHarness.NextUsn + 100, producedBlock.Header.UsnNextUsn,
                "the block is still mapped while the watch drains");
            applying.Release();
            await dispose.WaitAsync(HangGuard, TestContext.CancellationToken);

            Assert.AreEqual(1, handle.DisposeCount);
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = producedBlock.Header.Generation);

            using (holdCacheFile
                       ? new FileStream(Path.Combine(harness.CacheDirectory, "held.tmp"),
                           FileMode.Create, FileAccess.ReadWrite, FileShare.None)
                       : null)
            {
                harness.Dispose();
            }

            // A second disposal after the outsider releases must remove the tolerated remnant.
            harness.Dispose();
            Assert.IsFalse(Directory.Exists(harness.CacheDirectory));
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandleEndingWithoutAStop_StopReportsFailedResultUnlessFreshStartSupersedesIt(
        bool restartBeforeStop)
    {
        using var harness = new WatchHarness('T');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var ended = harness.Source.WatchFor('T');

        ended.End();
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        await ended.Disposed.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(1, harness.Faults);
        Assert.IsInstanceOfType<InvalidOperationException>(fault.Exception);
        Assert.AreEqual("The watch for drive T ended without being stopped.", fault.Exception.Message);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.IsNotNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(1, ended.DisposeCount);

        if (restartBeforeStop)
        {
            await harness.IndexFor('T').StartWatchingAsync(Token)
                .WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.HasCount(2, harness.Source.TargetsFor('T'));
            Assert.AreNotSame(ended, harness.Source.WatchFor('T'));
            Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
            Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
            await harness.IndexFor('T').StopWatchingAsync(Token)
                .WaitAsync(HangGuard, TestContext.CancellationToken);
        }
        else
        {
            var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => harness.IndexFor('T').StopWatchingAsync(Token)
                    .WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.AreSame(fault.Exception, failure);
        }

        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.NotApplicableAsync(
            () => harness.IndexFor('T').StopWatchingAsync(Token));

        await harness.IndexFor('T').StartWatchingAsync(Token)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('T').StopWatchingAsync(Token)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.HasCount(1, harness.Faults);
        foreach (var handle in harness.Source.Watches)
        {
            Assert.AreEqual(1, handle.DisposeCount);
        }
    }

    [TestMethod]
    public async Task StartWatchingAsync_AfterStop_RecordsFaultsForTheNewWatch()
    {
        using var harness = new WatchHarness();
        void FirstThrowingSubscriber(FileChange _) => throw new InvalidOperationException("first failure");
        harness.Index.Changed += FirstThrowingSubscriber;

        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));

        harness.Index.Changed -= FirstThrowingSubscriber;
        harness.Index.Changed += _ => throw new InvalidOperationException("second failure");
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        var secondFault = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.IndexFor('T').StopWatchingAsync(Token));

        Assert.AreEqual("second failure", secondFault.Message);
        Assert.HasCount(2, harness.Faults);
        Assert.AreEqual("second failure", harness.Faults[1].Exception.Message);
    }

    [TestMethod]
    public async Task DisposeAsync_ReleasesBlocksAfterASubscriberFault()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        var producedBlock = harness.BlockFor('T');

        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, harness.Source.WatchFor('T').DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = producedBlock.Header.Generation);
    }

    [TestMethod]
    public async Task StopWatchingAsync_HandleDisposalThrows_StopsTheDriveAndAllowsARestart()
    {
        var inner = new ScriptedWatchSource();
        var source = new ThrowingDisposalSource(inner);
        using var harness = new WatchHarness(source, 'T');
        await harness.IndexFor('T').StartWatchingAsync(Token);

        await harness.IndexFor('T').StopWatchingAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(1, source.DisposalAttempts, "the failing disposal ran once");
        Assert.AreEqual(1, inner.WatchFor('T').DisposeCount);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        Assert.IsEmpty(harness.Faults, "a teardown failure is not a watch fault");
        await harness.IndexFor('T').StartWatchingAsync(Token);
        Assert.HasCount(2, inner.TargetsFor('T'));
    }

    /// <summary>Hands out the wrapped source's handles, whose disposal disposes the inner handle and then throws.</summary>
    sealed class ThrowingDisposalSource(ScriptedWatchSource inner) : IIndexWatchSource
    {
        int _disposalAttempts;

        public int DisposalAttempts => Volatile.Read(ref _disposalAttempts);

        public async Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
        {
            var handle = await inner.StartAsync(target, cancellationToken);
            return new ThrowingDisposalHandle(handle.IndexWatch, () => Interlocked.Increment(ref _disposalAttempts));
        }
    }

    sealed class ThrowingDisposalHandle(IIndexDriveWatch inner, Action disposing) : IIndexDriveWatch
    {
        public IAsyncEnumerable<WatchStreamItem> ReadAsync(CancellationToken cancellationToken) =>
            inner.ReadAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            disposing();
            await inner.DisposeAsync();
            throw new IOException("the handle's teardown failed");
        }
    }
}
