using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchPumpTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestMethod]
    public async Task StartWatchingAsync_EachDriveStartsFromItsOwnHeaderCursor()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.SetNextProducedCursor('T', journalIdentifier: 11, nextUsn: 4242);
        harness.SetNextProducedCursor('U', journalIdentifier: 22, nextUsn: 8484);
        await harness.Index.RescanAsync(['T'], Token);
        await harness.Index.RescanAsync(['U'], Token);

        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Index.StartWatchingAsync(['U'], Token);

        CollectionAssert.AreEqual(
            new[] { new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484) },
            harness.Source.Targets.ToArray());
        await harness.Index.StopWatchingAsync(['T'], Token);
        await harness.Index.StopWatchingAsync(['U'], Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_SurfacesASubscriberFaultAndKeepsPumpingUntilThen()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.Index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));

        var fault = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));
        Assert.AreEqual("subscriber failed", fault.Message);
        Assert.AreEqual(2, delivered);
    }

    [TestMethod]
    public async Task WatchFaulted_AnnouncesTheFirstSubscriberFaultImmediatelyAndOnlyOnce()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));

        var faults = harness.Faults;
        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);
        Assert.AreEqual('T', faults[0].Drive);
        Assert.AreEqual("subscriber failed", faults[0].Exception.Message);

        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        Assert.AreEqual(1, harness.Faults.Count);

        await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));
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
            () => harness.Index.StartWatchingAsync(['T'], Token));

        Assert.AreSame(sourceFault, failure);
        Assert.AreEqual(0, harness.Faults.Count, "reported once, through the start");
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual("source aborted", harness.DriveFor('T').Watch.FailureMessage);

        // The failed start leaves the watch requested with no instance; stop clears the faulted state.
        await harness.Index.StopWatchingAsync(['T'], Token);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
    }

    [TestMethod]
    public async Task WatchFaulted_DoesNotFireForAnOrdinaryStop()
    {
        using var harness = new WatchHarness();

        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await harness.Index.StopWatchingAsync(['T'], Token);

        Assert.AreEqual(0, harness.Faults.Count);
    }

    [TestMethod]
    public async Task WatchFaulted_SwallowsAThrowingFaultHandlerAndStopReportsTheOriginalFailure()
    {
        using var harness = new WatchHarness();
        harness.Index.WatchFaulted += _ => throw new NotSupportedException("fault handler failed");
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));

        var fault = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));
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

        await harness.Index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, harness.Faults[0].Kind);

        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        var fault = await DriveResultAssertions.FailureAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));

        Assert.AreSame(subscriberFault, fault);
        Assert.AreEqual(2, delivered);
        Assert.AreEqual(1, harness.Faults.Count);
    }

    [TestMethod]
    public async Task DisposeAsync_StopsTheWatchBeforeReleasingBlocks()
    {
        using var harness = new WatchHarness();
        var applying = harness.TrackGate();
        harness.Index.Changed += _ =>
        {
            applying.MarkEntered();
            applying.WaitForRelease();
        };
        await harness.Index.StartWatchingAsync(['T'], Token);
        var handle = harness.Source.WatchFor('T');
        _ = handle.Queue(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await applying.Entered.WaitAsync(HangGuard);
        var producedBlock = harness.BlockFor('T');

        var dispose = harness.Index.DisposeAsync().AsTask();

        Assert.IsFalse(dispose.IsCompleted, "disposal waits for the pump that is still applying");
        Assert.AreEqual(WatchHarness.NextUsn + 100, producedBlock.Header.UsnNextUsn,
            "the block is still mapped while the watch drains");
        applying.Release();
        await dispose.WaitAsync(HangGuard);

        Assert.AreEqual(1, handle.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = producedBlock.Header.Generation);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandleEndingWithoutAStop_StopReportsFailedResultUnlessFreshStartSupersedesIt(
        bool restartBeforeStop)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var ended = harness.Source.WatchFor('T');

        ended.End();
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        await ended.Disposed.WaitAsync(HangGuard);

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.IsInstanceOfType<InvalidOperationException>(fault.Exception);
        Assert.AreEqual("The watch for drive T ended without being stopped.", fault.Exception.Message);
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.IsNotNull(harness.DriveFor('T').Watch.FailureMessage);
        Assert.AreEqual(1, ended.DisposeCount);

        if (restartBeforeStop)
        {
            await harness.Index.StartWatchingAsync(['T'], Token)
                .WaitAsync(HangGuard);
            Assert.AreEqual(2, harness.Source.TargetsFor('T').Count);
            Assert.AreNotSame(ended, harness.Source.WatchFor('T'));
            Assert.AreEqual(WatchState.CatchingUp, harness.DriveFor('T').Watch.State);
            Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
            await harness.Index.StopWatchingAsync(['T'], Token)
                .WaitAsync(HangGuard);
        }
        else
        {
            var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
                () => harness.Index.StopWatchingAsync(['T'], Token)
                    .WaitAsync(HangGuard));
            Assert.AreSame(fault.Exception, failure);
        }

        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        await DriveResultAssertions.NotApplicableAsync(
            () => harness.Index.StopWatchingAsync(['T'], Token));

        await harness.Index.StartWatchingAsync(['T'], Token)
            .WaitAsync(HangGuard);
        await harness.Index.StopWatchingAsync(['T'], Token)
            .WaitAsync(HangGuard);
        Assert.AreEqual(1, harness.Faults.Count);
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

        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));

        harness.Index.Changed -= FirstThrowingSubscriber;
        harness.Index.Changed += _ => throw new InvalidOperationException("second failure");
        await harness.Index.StartWatchingAsync(['T'], Token);
        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        var secondFault = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(['T'], Token));

        Assert.AreEqual("second failure", secondFault.Message);
        Assert.AreEqual(2, harness.Faults.Count);
        Assert.AreEqual("second failure", harness.Faults[1].Exception.Message);
    }

    [TestMethod]
    public async Task DisposeAsync_ReleasesBlocksAfterASubscriberFault()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        await harness.Index.StartWatchingAsync(['T'], Token);
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
        await harness.Index.StartWatchingAsync(['T'], Token);

        await harness.Index.StopWatchingAsync(['T'], Token).WaitAsync(HangGuard);

        Assert.AreEqual(1, source.DisposalAttempts, "the failing disposal ran once");
        Assert.AreEqual(1, inner.WatchFor('T').DisposeCount);
        Assert.AreEqual(WatchState.NotStarted, harness.DriveFor('T').Watch.State);
        Assert.AreEqual(0, harness.Faults.Count, "a teardown failure is not a watch fault");
        await harness.Index.StartWatchingAsync(['T'], Token);
        Assert.AreEqual(2, inner.TargetsFor('T').Count);
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
