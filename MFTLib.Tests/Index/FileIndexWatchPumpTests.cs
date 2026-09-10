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
    public async Task StartWatchingAsync_PumpsBatchesIntoTheIndexAndRaisesChanged()
    {
        using var harness = new WatchHarness();
        var changes = new List<FileChange>();
        harness.Index.Changed += change => changes.Add(change);

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "new.txt", parentRecordNumber: 5)], JournalId: 7, NextUsn: 100));
        await harness.Index.StopWatchingAsync(Token);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(FileChangeKind.Created, changes[0].Kind);
        Assert.AreEqual("new.txt", changes[0].Entry.Name);
    }

    [TestMethod]
    public async Task StartWatchingAsync_ResumesEachDriveFromItsHeaderCursor()
    {
        using var harness = new WatchHarness(journalId: 11, nextUsn: 4242);
        await harness.Index.StartWatchingAsync(Token);

        var target = (await harness.SourceStartedAsync()).Single();
        Assert.AreEqual('T', target.DriveLetter);
        Assert.AreEqual(11ul, target.JournalId);
        Assert.AreEqual(4242L, target.NextUsn);

        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_PassesEveryDriveCursorToOneSourceInvocation()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);

        var targets = await harness.SourceStartedAsync();
        Assert.AreEqual(1, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(
            new[] { new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484) },
            targets.ToArray());

        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_ThrowsWhenADriveSupportsWatchAndNoSourceIsConfigured()
    {
        using var harness = new WatchHarness(watchSource: null);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StartWatchingAsync(Token));
    }

    [TestMethod]
    public async Task StopWatchingAsync_SurfacesASubscriberFaultAndKeepsPumpingUntilThen()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 10, "two.txt"));

        var fault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("subscriber failed", fault.Message);
        Assert.AreEqual(2, delivered);
    }

    [TestMethod]
    public async Task WatchFaulted_AnnouncesTheFirstSubscriberFaultImmediatelyAndOnlyOnce()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += fault => faults.Add(fault);
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));

        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);
        Assert.AreEqual('T', faults[0].DriveLetter);
        Assert.AreEqual("subscriber failed", faults[0].Exception.Message);

        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 10, "two.txt"));
        Assert.AreEqual(1, faults.Count);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task WatchFaulted_AnnouncesASourceFaultWithNoDriveLetter()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += fault => faults.Add(fault);

        await harness.Index.StartWatchingAsync(Token);
        await harness.FaultSourceAsync(new IOException("the broker died"));

        var fault = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("the broker died", fault.Message);
        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Source, faults[0].Kind);
        Assert.IsNull(faults[0].DriveLetter);
    }

    [TestMethod]
    public async Task WatchFaulted_ReportsAnIndependentSourceOperationCanceledException()
    {
        var sourceFault = new OperationCanceledException("source aborted");
        using var harness = new WatchHarness(new ThrowingWatchSource(sourceFault));
        var faults = new List<WatchFault>();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.WatchFaulted += fault =>
        {
            faults.Add(fault);
            announced.TrySetResult();
        };

        await harness.Index.StartWatchingAsync(Token);

        // Waiting for the announcement, not for time: the pump yields before it touches the
        // source, so stopping first would cancel the session before the source ever threw.
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var thrown = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync(Token));

        Assert.AreSame(sourceFault, thrown);
        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Source, faults[0].Kind);
        Assert.IsNull(faults[0].DriveLetter);
    }

    [TestMethod]
    public async Task WatchFaulted_DoesNotFireForAnOrdinaryStop()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += fault => faults.Add(fault);

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
        await harness.Index.StopWatchingAsync(Token);

        Assert.AreEqual(0, faults.Count);
    }

    [TestMethod]
    public async Task WatchFaulted_SwallowsAThrowingFaultHandlerAndStillRethrowsTheOriginal()
    {
        using var harness = new WatchHarness();
        harness.Index.WatchFaulted += _ => throw new NotSupportedException("fault handler failed");
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));

        var fault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("subscriber failed", fault.Message);
    }

    [TestMethod]
    public async Task SubscriberOperationCanceledException_IsReportedAndRethrownAfterPumpingContinues()
    {
        using var harness = new WatchHarness();
        var subscriberFault = new OperationCanceledException("subscriber cancelled");
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        harness.Index.Changed += _ => throw subscriberFault;
        var delivered = 0;
        harness.Index.Changed += _ => delivered++;

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Subscriber, faults[0].Kind);

        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 10, "two.txt"));
        var fault = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => harness.Index.StopWatchingAsync(Token));

        Assert.AreSame(subscriberFault, fault);
        Assert.AreEqual(2, delivered);
        Assert.AreEqual(1, faults.Count);
    }

    [TestMethod]
    public async Task DisposeAsync_StopsTheWatchBeforeReleasingBlocks()
    {
        var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.Index.DisposeAsync();

        Assert.IsTrue(harness.SourceCancelled);
        Assert.IsTrue(harness.SourceStoppedBeforeBlockDisposed);
        harness.Dispose();
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenAlreadyWatching_ThrowsInvalidOperation()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(Token);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StartWatchingAsync(Token));

        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StopWatchingAsync_BeforeAWatchStarts_HasNoEffect()
    {
        using var harness = new WatchHarness();
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task SourceCompletingWithoutAStop_FaultsEveryWatchedDriveAndReleasesTheSession()
    {
        using var harness = new WatchHarness();
        var faults = new List<WatchFault>();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.WatchFaulted += fault =>
        {
            faults.Add(fault);
            announced.TrySetResult();
        };

        await harness.Index.StartWatchingAsync(Token);
        await harness.CompleteSourceAsync();

        // Waiting for the announcement, not for time: the source iterator's own end runs before
        // the pump observes it, so the assertions below would race the report.
        await announced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);

        // Nothing is watching once the stream ends, so a drive that still read as healthy would
        // say the opposite of the truth. The end belongs to the session, hence no drive letter.
        Assert.IsFalse(harness.SourceCancelled);
        Assert.AreEqual(1, faults.Count);
        Assert.AreEqual(WatchFaultKind.Source, faults[0].Kind);
        Assert.IsNull(faults[0].DriveLetter);
        Assert.IsNotNull(harness.Index.Drives.Single(drive => drive.DriveLetter == 'T').WatchFailureMessage);

        // The session claim is released with it, so a consumer recovers by starting a fresh one
        // rather than having to stop a session that is already over.
        await harness.Index.StartWatchingAsync(Token);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_AfterStop_RecordsFaultsForTheNewSession()
    {
        using var harness = new WatchHarness();
        void FirstThrowingSubscriber(FileChange _) => throw new InvalidOperationException("first failure");
        harness.Index.Changed += FirstThrowingSubscriber;
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;

        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));

        harness.Index.Changed -= FirstThrowingSubscriber;
        harness.Index.Changed += _ => throw new InvalidOperationException("second failure");
        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 10, "two.txt"));
        var secondFault = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));

        Assert.AreEqual("second failure", secondFault.Message);
        Assert.AreEqual(2, faults.Count);
        Assert.AreEqual("second failure", faults[1].Exception.Message);
    }

    [TestMethod]
    public async Task DisposeAsync_ReleasesBlocksAfterASubscriberFault()
    {
        using var harness = new WatchHarness();
        harness.Index.Changed += _ => throw new InvalidOperationException("subscriber failed");
        await harness.Index.StartWatchingAsync(Token);
        await harness.PublishAsync(WatchHarness.Batch('T', recordNumber: 9, "one.txt"));
        var producedBlock = harness.BlockFor('T');

        await harness.Index.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsTrue(harness.SourceCancelled);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = producedBlock.Header.Generation);
    }

    /// <summary>A source whose every member fails the session it is handed to.</summary>
    sealed class ThrowingWatchSource(Exception failure) : IIndexWatchSource
    {
        public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
            CancellationToken cancellationToken) => throw failure;

        public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken) => throw failure;

        public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken) => throw failure;
    }
}
