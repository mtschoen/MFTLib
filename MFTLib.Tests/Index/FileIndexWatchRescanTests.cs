// Over the size guideline on purpose: one cohesive fixture whose cases share the cache-only options and the rescan helpers.
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan while a watch is running acts on the rescanned root alone: its watch is retired
///     and started again from the fresh cursor, and every other root's pump keeps reading.
/// </summary>
[TestClass]
public partial class FileIndexWatchRescanTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    protected override bool SeedDocuments => false;

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(CacheDirectoryPath);

    [TestMethod]
    public async Task RescanAsync_WhileWatching_RestartsOnlyTheRescannedRootFromTheFreshCursorAndClearsItsFailure()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var unrescannedHandle = harness.Source.WatchFor('U');
        harness.Source.WatchFor('T').FailVolume(new IOException("journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        Assert.IsNotNull(harness.RootFor('T').Watch.FailureMessage);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);

        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(2, starts);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9000), starts[1]);
        Assert.HasCount(1, harness.Source.TargetsFor('U'));
        Assert.AreEqual(0, unrescannedHandle.DisposeCount);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);

        await harness.Source.WatchFor('T').Publish(
            new JournalBatch([WatchHarness.Create(9, "after.txt")], 13, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_NeverStopsTheOtherRoot()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var otherHandle = harness.Source.WatchFor('U');
        await otherHandle.Publish(WatchHarness.Batch(9, "before.txt", nextUsn: 150));
        Assert.AreEqual(150L, harness.BlockFor('U').Header.UsnNextUsn);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);

        await otherHandle.Publish(WatchHarness.Batch(10, "after.txt", nextUsn: 250));

        Assert.HasCount(1, harness.Source.TargetsFor('U'));
        Assert.AreEqual(0, otherHandle.DisposeCount);
        Assert.AreEqual(250L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.AreSequenceEqual(["before.txt", "after.txt"], [.. harness.Changes.Select(change => change.Entry.Name)]);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task Rescan_OfT_LeavesUsPumpRunning()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var otherHandle = harness.Source.WatchFor('U');
        var producing = harness.HoldNextProduction('T');
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await producing.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await otherHandle.Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 555));

        Assert.IsFalse(rescan.IsCompleted, "T's producer is still gated");
        Assert.Contains(change => change.Entry.Name == "u.txt", harness.Changes, "U applied while T's scan ran");
        Assert.AreEqual(555L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.AreEqual(0, otherHandle.DisposeCount);
        producing.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_OfAHealthyRoot_WhenTheOtherRootFaultsMidScan_RestartsItAndLeavesTheOtherFaulted()
    {
        using var harness = new WatchHarness('T', 'U');
        var lostChannel = new IOException("U's channel was lost");
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        var production = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        // T's watch is running and carries no failure, so U dropping mid-scan does not touch it.
        harness.Source.WatchFor('U').LoseChannel(lostChannel);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'U');
        production.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.HasCount(2, harness.Source.TargetsFor('T'));
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9000), harness.Source.TargetsFor('T')[1]);
        Assert.HasCount(1, harness.Source.TargetsFor('U'));
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, harness.RootFor('T').Watch.State);
        Assert.AreEqual("U's channel was lost", harness.RootFor('U').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, harness.RootFor('U').Watch.State);

        await harness.Source.WatchFor('T').Publish(
            new JournalBatch([WatchHarness.Create(9, "after.txt")], 13, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        var failure = await RootResultAssertions.FailureAsync<IOException>(() => harness.IndexFor('U').StopWatchingAsync(Token));
        Assert.AreSame(lostChannel, failure);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_DropsABatchThePumpHadAlreadyAcceptedWhenTheRootWasRetired()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var applying = harness.HoldFirstApply('T');
        var queued = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "stale.txt", nextUsn: 5000));
        await applying.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        var publication = IndexPublication.ObserveNextAsync(harness.IndexFor('T'));
        var rescan = harness.IndexFor('T').RescanAsync(Token);
        await publication;
        Assert.IsFalse(rescan.IsCompleted, "the rescan waits for the retiring pump to drain");
        applying.Release();
        await rescan.WaitAsync(HangGuard, TestContext.CancellationToken);
        await queued.WaitAsync(HangGuard, TestContext.CancellationToken);

        // The pump had already read the batch, so no source-side rule can reach it: the scoping
        // rule drops it because its instance is no longer the root's current one.
        Assert.IsEmpty(harness.Changes);
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalIdentifier);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.IndexFor('T').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_AfterEveryRootFaulted_RestartsOnlyTheRescannedRootAndLeavesTheOthersFaulted()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.HoldEveryRecovery();
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);
        harness.Source.WatchFor('T').FailVolume(new IOException("T's journal wrapped"));
        harness.Source.WatchFor('U').FailVolume(new IOException("U's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'T');
        var uFault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 'U');

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);

        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(2, starts);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 13, 9000), starts[1]);
        Assert.HasCount(1, harness.Source.TargetsFor('U'), "U's own failure still stands, so it needs its own rescan.");
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
        Assert.AreEqual(uFault.Exception.Message, harness.RootFor('U').Watch.FailureMessage);
        Assert.AreEqual(WatchState.Recovering, harness.RootFor('U').Watch.State, "U's recovery is still held");

        await harness.Source.WatchFor('T').Publish(
            new JournalBatch([WatchHarness.Create(10, "t.txt")], 13, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);

        await harness.IndexFor('T').StopWatchingAsync(Token);
        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => harness.IndexFor('U').StopWatchingAsync(Token));
        Assert.AreSame(uFault.Exception, failure);
    }

    [TestMethod]
    public async Task RescanAsync_WithNoWatchRunning_StartsNoWatch()
    {
        using var harness = new WatchHarness('T', 'U');

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.IndexFor('T').RescanAsync(Token);

        Assert.IsEmpty(harness.Source.Starts);
        var root = harness.RootFor('T');
        Assert.IsFalse(root.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, root.Watch.State);
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalIdentifier);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        await RootResultAssertions.NotApplicableAsync(() => harness.IndexFor('T').WaitForCatchUpAsync(Token));
    }

    [TestMethod]
    public async Task RescanAsync_WhoseProducerThrowsCancellation_KeepsTheRootWatchingAndReportsFailedResult()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        await harness.IndexFor('U').StartWatchingAsync(Token);

        var scanFailure = new OperationCanceledException("the scan was cancelled");
        harness.FailNextProduction('T', scanFailure);
        var failure = await RootResultAssertions.FailureAsync<OperationCanceledException>(() => harness.IndexFor('T').RescanAsync(Token));

        Assert.AreSame(scanFailure, failure);

        // The block was never swapped, so the original watch keeps its cursor and catch-up.
        var starts = harness.Source.TargetsFor('T');
        Assert.HasCount(1, starts);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), WatchHarness.JournalIdentifier, WatchHarness.NextUsn), starts[0]);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "after.txt", nextUsn: 5000));
        Assert.AreEqual(5000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.IndexFor('T').StopWatchingAsync(Token);
        await harness.IndexFor('U').StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledWhileAwaitingWriteGate_DisposesCompletedScanAndRestoresRetiredCache()
    {
        using var harness = new WatchHarness('T');

        await harness.IndexFor('T').WaitForRootWriteGateForTest(new VolumeKey('T'), TestContext.CancellationToken);
        try
        {
            var initialBlock = harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock;
            var initialProducedBlock = harness.BlockFor('T');
            using var rescanCancellation = new CancellationTokenSource();
            harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);

            var rescan = harness.IndexFor('T').RescanAsync(rescanCancellation.Token);
            var producedBlock = await WaitForReplacementBlockAsync(harness, 'T', initialProducedBlock, rescan);
            await rescanCancellation.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard, TestContext.CancellationToken));

            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = producedBlock.Header);
            Assert.AreSame(initialBlock, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock);
            Assert.IsEmpty(Directory.GetFiles(harness.CacheDirectory, "*.retired-*"));
        }
        finally
        {
            harness.IndexFor('T').ReleaseRootWriteGateForTest(new VolumeKey('T'));
        }
    }

    /// <summary>
    ///     Two MFT volumes over one temp tree: 'U' gets a valid block pre-written at its canonical
    ///     cache path (cursor 22/8484) so a cache-only open warm-starts it, and 'T' gets none, so
    ///     the same open declines it. The producer runs only on a rescan of 'T'.
    /// </summary>
    FileIndexOptions CacheOnlyWatchOptions(ScriptedWatchSource source, char driveLetter, bool failRootT)
    {
        SeededBlocks.Write(Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('U'), 2)),
            volumeSerial: 2, journalIdentifier: 22, nextUsn: 8484, moment: SeededBlocks.SeededMoment);
        return new FileIndexOptions
        {

            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource((request, cancellationToken) =>
            {
                if (failRootT && char.ToUpperInvariant((request.Volume).Letter) == 'T')
                {
                    throw new UnauthorizedAccessException("elevation declined");
                }

                SeededBlocks.Write(request.BlockPath, request.VolumeSerial, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
                return Task.FromResult(new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    SkippedRecordCount: 0));
            }, source.IndexSource),
            CacheMode = CacheMode.ExistingOnly
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey(driveLetter), TreeRoot, driveLetter == 'T' ? 1u : 2u));
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRoot_AdoptsItWithoutAWatchAndALaterStartAppliesItsBatches()
    {
        var source = new ScriptedWatchSource();
        await using var indexT = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: false), Token);
        await using var indexU = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'U', failRootT: false), Token);
        var declined = indexT.Status;
        Assert.AreEqual(RootState.Failed, declined.State);
        Assert.AreEqual(RootFailureKind.CacheDeclined, declined.Failure?.Kind);
        await indexU.StartWatchingAsync(Token);

        await indexT.RescanAsync(Token);

        // 'T' never asked to be watched, so its adoption starts nothing.
        Assert.IsEmpty(source.TargetsFor('T'));
        var recovered = indexT.Status;
        Assert.AreEqual(RootState.Ready, recovered.State);
        Assert.IsNull(recovered.Failure);
        Assert.AreEqual(WatchState.NotStarted, recovered.Watch.State);

        await indexT.StartWatchingAsync(Token);
        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), 7, 4096), source.TargetsFor('T').Single());
        Assert.AreEqual(WatchState.CatchingUp, indexT.Status.Watch.State);

        await source.WatchFor('T').Publish(new JournalBatch([WatchHarness.Create(9, "after.txt")], 7, 5000));
        Assert.AreEqual(5000L, indexT.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);

        var caughtUp = indexT.WaitForCatchUpAsync(Token);
        await source.WatchFor('T').Publish(new VolumeCaughtUp());
        await caughtUp.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.CaughtUp, indexT.Status.Watch.State);
        await indexT.StopWatchingAsync(Token);
        await indexU.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhileWatching_WhoseScanFails_StaysFailedAndKeepsTheWatchUntouched()
    {
        var source = new ScriptedWatchSource();
        await using var indexT = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: true), Token);
        await using var indexU = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'U', failRootT: true), Token);
        await indexU.StartWatchingAsync(Token);

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => indexT.RescanAsync(Token));

        Assert.Contains("elevation declined", failure.Message);
        var status = indexT.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual("elevation declined", status.Failure?.Message);
        Assert.IsEmpty(source.TargetsFor('T'), "a still-blockless root is never watched");

        // The other root never noticed.
        Assert.AreEqual(0, source.WatchFor('U').DisposeCount);
        await source.WatchFor('U').Publish(new JournalBatch([WatchHarness.Create(9, "u.txt")], 22, 8500));
        Assert.AreEqual(8500L, indexU.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        await indexU.StopWatchingAsync(Token);
        await RootResultAssertions.NotApplicableAsync(() => indexT.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhileWatching_RescannedASecondTime_RestartsIt()
    {
        var source = new ScriptedWatchSource();
        await using var indexT = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: false), Token);
        await using var indexU = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'U', failRootT: false), Token);
        await indexU.StartWatchingAsync(Token);
        await indexT.RescanAsync(Token);
        await indexT.StartWatchingAsync(Token);
        var firstHandle = source.WatchFor('T');

        // 'T' is now watching, so the rescan retires its watch at publication and starts it
        // again afterwards.
        await indexT.RescanAsync(Token);

        Assert.HasCount(2, source.TargetsFor('T'));
        Assert.AreEqual(1, firstHandle.DisposeCount);
        await source.WatchFor('T').Publish(new JournalBatch([WatchHarness.Create(10, "second.txt")], 7, 6000));
        Assert.AreEqual(6000L, indexT.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        await indexT.StopWatchingAsync(Token);
        await indexU.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhileWatching_WhenOnlyInitialRootFaults_KeepsWatchingAdoptedRoot()
    {
        var source = new ScriptedWatchSource();
        await using var indexT = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: false), Token);
        await using var indexU = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'U', failRootT: false), Token);
        indexU.HoldEveryRecovery();
        await indexU.StartWatchingAsync(Token);
        await indexT.RescanAsync(Token);
        await indexT.StartWatchingAsync(Token);

        var faulted = NextFaultOf(indexU, 'U');
        source.WatchFor('U').FailVolume(new IOException("U journal error"));
        var fault = await faulted.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsNotNull(indexU.Status.Watch.FailureMessage);

        await source.WatchFor('T').Publish(new JournalBatch([WatchHarness.Create(9, "surviving.txt")], 7, 5500));
        Assert.AreEqual(5500L, indexT.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        Assert.IsNull(indexT.Status.Watch.FailureMessage);

        var failure = await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => indexU.StopWatchingAsync(Token));
        Assert.AreSame(fault.Exception, failure);
        await indexT.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhileWatching_StreamEndsWithoutStop_FaultsAdoptedRootCatchUp()
    {
        var source = new ScriptedWatchSource();
        await using var indexT = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: false), Token);
        await using var indexU = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'U', failRootT: false), Token);
        await indexU.StartWatchingAsync(Token);
        await indexT.RescanAsync(Token);
        await indexT.StartWatchingAsync(Token);

        var catchUpTask = indexT.WaitForCatchUpAsync(Token);
        source.WatchFor('T').End();

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => catchUpTask.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.Contains("ended without being stopped", failure.Message);
        Assert.AreEqual(WatchState.Faulted, indexT.Status.Watch.State);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhileWatching_UnhandledSourceException_FaultsAdoptedRootCatchUp()
    {
        var source = new ScriptedWatchSource();
        await using var indexT = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'T', failRootT: false), Token);
        await using var indexU = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, 'U', failRootT: false), Token);
        await indexU.StartWatchingAsync(Token);
        await indexT.RescanAsync(Token);
        await indexT.StartWatchingAsync(Token);

        var catchUpTask = indexT.WaitForCatchUpAsync(Token);
        var sourceException = new InvalidOperationException("stream crashed");
        source.WatchFor('T').LoseChannel(sourceException);

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => catchUpTask.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreSame(sourceException, failure);
        Assert.AreEqual(WatchState.Faulted, indexT.Status.Watch.State);
    }

    /// <summary>
    ///     Polls until the producer has handed back a block other than <paramref name="original" />
    ///     for the root, which is the moment the rescan holds a finished, unpublished scan.
    /// </summary>
    internal static async Task<BlockFile> WaitForReplacementBlockAsync(WatchHarness harness, char driveLetter,
        BlockFile original, Task rescan)
    {
        using var timeout = new CancellationTokenSource(HangGuard);
        while (ReferenceEquals(harness.BlockFor(driveLetter), original))
        {
            Assert.IsFalse(rescan.IsCompleted, "the rescan ended before its producer returned a block");
            await Task.Delay(10, timeout.Token);
        }

        return harness.BlockFor(driveLetter);
    }

    /// <summary>Completes with the first fault the index raises for the root after this call.</summary>
    static Task<WatchFault> NextFaultOf(FileIndex index, char driveLetter)
    {
        var completion = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Volume == new VolumeKey(driveLetter))
            {
                completion.TrySetResult(fault);
            }
        };
        return completion.Task;
    }

}
