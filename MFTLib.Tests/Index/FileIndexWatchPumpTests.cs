using MFTLib.Index;
using MFTLib.Tests.TestSupport;
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
        harness.SetNextProducedCursor('T', journalId: 11, nextUsn: 4242);
        harness.SetNextProducedCursor('U', journalId: 22, nextUsn: 8484);
        await harness.Index.RescanAsync('T', Token);
        await harness.Index.RescanAsync('U', Token);

        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);

        CollectionAssert.AreEqual(
            new[] { new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484) },
            harness.Source.Starts.ToArray());
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_SurfacesASubscriberFaultAndKeepsPumpingUntilThen()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));

        var fault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreEqual("subscriber failed", fault.Message);
        Assert.AreEqual(2, delivered);
    }

    [TestMethod]
    public async Task WatchFaulted_AnnouncesTheFirstSubscriberFaultImmediatelyAndOnlyOnce()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));

        var faults = harness.Faults;
        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);
        Assert.AreEqual('T', faults[0].DriveLetter);
        Assert.AreEqual("subscriber failed", faults[0].Exception.Message);

        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        Assert.AreEqual(1, harness.Faults.Count);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task StartWatchingAsync_ReportsAnIndependentSourceOperationCanceledExceptionThroughTheStart()
    {
        using var harness = new WatchHarness();
        var sourceFault = new OperationCanceledException("source aborted");
        harness.Source.FailStart(sourceFault);

        // The source throws before it returns a handle, so the start itself reports the failure,
        // as the source's own exception rather than as a cancellation of this call.
        var thrown = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => harness.Index.StartWatchingAsync('T', Token));

        Assert.AreSame(sourceFault, thrown);
        Assert.AreEqual(0, harness.Faults.Count, "reported once, through the start");
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual("source aborted", harness.DriveFor('T').WatchFailureMessage);

        // The failed start leaves the watch requested with no instance, so a stop has nothing to
        // rethrow and clears the faulted state.
        await harness.Index.StopWatchingAsync('T', Token);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
    }

    [TestMethod]
    public async Task WatchFaulted_DoesNotFireForAnOrdinaryStop()
    {
        using var harness = new WatchHarness();

        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await harness.Index.StopWatchingAsync('T', Token);

        Assert.AreEqual(0, harness.Faults.Count);
    }

    [TestMethod]
    public async Task WatchFaulted_SwallowsAThrowingFaultHandlerAndStillRethrowsTheOriginal()
    {
        using var harness = new WatchHarness();
        harness.Index.WatchFaulted += _ => throw new NotSupportedException("fault handler failed");
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));

        var fault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreEqual("subscriber failed", fault.Message);
    }

    [TestMethod]
    public async Task SubscriberOperationCanceledException_IsReportedAndRethrownAfterPumpingContinues()
    {
        using var harness = new WatchHarness();
        var subscriberFault = new OperationCanceledException("subscriber cancelled");
        harness.Index.Changed += _ => throw subscriberFault;
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        await handle.Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        Assert.AreEqual(1, harness.Faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, harness.Faults[0].Kind);

        await handle.Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        var fault = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync('T', Token));

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
        await harness.Index.StartWatchingAsync('T', Token);
        var handle = harness.Source.HandleFor('T');
        _ = handle.Queue(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await applying.Entered.WaitAsync(FakeIndexWatchSource.HangGuard);
        var producedBlock = harness.BlockFor('T');

        var dispose = harness.Index.DisposeAsync().AsTask();

        Assert.IsFalse(dispose.IsCompleted, "disposal waits for the pump that is still applying");
        Assert.AreEqual(WatchHarness.NextUsn + 100, producedBlock.Header.UsnNextUsn,
            "the block is still mapped while the watch drains");
        applying.Release();
        await dispose.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(1, handle.DisposeCount);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = producedBlock.Header.Generation);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandleEndingWithoutAStop_FaultIsRethrownUnlessFreshStartSupersedesIt(
        bool restartBeforeStop)
    {
        using var harness = new WatchHarness('T');
        await harness.Index.StartWatchingAsync('T', Token);
        var ended = harness.Source.HandleFor('T');

        ended.End();
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        await ended.Disposed.WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(1, harness.Faults.Count);
        Assert.IsInstanceOfType<InvalidOperationException>(fault.Exception);
        Assert.AreEqual("The watch for drive T ended without being stopped.", fault.Exception.Message);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('T').WatchCatchUp);
        Assert.IsNotNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.AreEqual(1, ended.DisposeCount);

        if (restartBeforeStop)
        {
            await harness.Index.StartWatchingAsync('T', Token)
                .WaitAsync(FakeIndexWatchSource.HangGuard);
            Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
            Assert.AreNotSame(ended, harness.Source.HandleFor('T'));
            Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
            Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
            await harness.Index.StopWatchingAsync('T', Token)
                .WaitAsync(FakeIndexWatchSource.HangGuard);
        }
        else
        {
            var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => harness.Index.StopWatchingAsync('T', Token)
                    .WaitAsync(FakeIndexWatchSource.HangGuard));
            Assert.AreSame(fault.Exception, thrown);
        }

        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
        var alreadyStopped = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));
        Assert.AreNotSame(fault.Exception, alreadyStopped);
        StringAssert.Contains(alreadyStopped.Message, "not watching");

        await harness.Index.StartWatchingAsync('T', Token)
            .WaitAsync(FakeIndexWatchSource.HangGuard);
        await harness.Index.StopWatchingAsync('T', Token)
            .WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(1, harness.Faults.Count);
        foreach (var handle in harness.Source.Handles)
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

        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));

        harness.Index.Changed -= FirstThrowingSubscriber;
        harness.Index.Changed += _ => throw new InvalidOperationException("second failure");
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(recordNumber: 10, "two.txt"));
        var secondFault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync('T', Token));

        Assert.AreEqual("second failure", secondFault.Message);
        Assert.AreEqual(2, harness.Faults.Count);
        Assert.AreEqual("second failure", harness.Faults[1].Exception.Message);
    }

    [TestMethod]
    public async Task DisposeAsync_ReleasesBlocksAfterASubscriberFault()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(recordNumber: 9, "one.txt"));
        var producedBlock = harness.BlockFor('T');

        await harness.Index.DisposeAsync();

        Assert.AreEqual(1, harness.Source.HandleFor('T').DisposeCount);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = producedBlock.Header.Generation);
    }

    [TestMethod]
    public async Task StopWatchingAsync_HandleDisposalThrows_StopsTheDriveAndAllowsARestart()
    {
        var inner = new FakeIndexWatchSource();
        var source = new ThrowingDisposalSource(inner);
        using var harness = new WatchHarness(source, 'T');
        await harness.Index.StartWatchingAsync('T', Token);

        await harness.Index.StopWatchingAsync('T', Token).WaitAsync(FakeIndexWatchSource.HangGuard);

        Assert.AreEqual(1, source.DisposalAttempts, "the failing disposal ran once");
        Assert.AreEqual(1, inner.HandleFor('T').DisposeCount);
        Assert.AreEqual(WatchCatchUpState.NotStarted, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual(0, harness.Faults.Count, "a teardown failure is not a watch fault");
        await harness.Index.StartWatchingAsync('T', Token);
        Assert.AreEqual(2, inner.StartsFor('T').Count);
    }

    /// <summary>Hands out the wrapped source's handles, whose disposal disposes the inner handle and then throws.</summary>
    sealed class ThrowingDisposalSource(FakeIndexWatchSource inner) : IIndexWatchSource
    {
        int _disposalAttempts;

        public int DisposalAttempts => Volatile.Read(ref _disposalAttempts);

        public async Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
        {
            var handle = await inner.StartAsync(target, cancellationToken);
            return new ThrowingDisposalHandle(handle, () => Interlocked.Increment(ref _disposalAttempts));
        }
    }

    sealed class ThrowingDisposalHandle(IIndexDriveWatch inner, Action disposing) : IIndexDriveWatch
    {
        public char DriveLetter => inner.DriveLetter;

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
