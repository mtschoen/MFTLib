// Over the size guideline on purpose: one cohesive fixture whose cases share the cache-only options and the rescan helpers.
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A rescan while a watch is running acts on the rescanned drive alone: its watch is retired
///     and started again from the fresh cursor, and every other drive's pump keeps reading.
/// </summary>
[TestClass]
public partial class FileIndexWatchRescanTests
{
    static readonly TimeSpan HangGuard = FakeIndexWatchSource.HangGuard;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_treeRoot);
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_RestartsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var unrescannedHandle = harness.Source.HandleFor('U');
        harness.Source.HandleFor('T').FailDrive(new IOException("journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        Assert.IsNotNull(harness.DriveFor('T').WatchFailureMessage);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        var starts = harness.Source.StartsFor('T');
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), starts[1]);
        Assert.AreEqual(1, harness.Source.StartsFor('U').Count);
        Assert.AreEqual(0, unrescannedHandle.DisposeCount);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);

        await harness.Source.HandleFor('T').Publish(
            new JournalBatch([WatchHarness.Create(9, "after.txt")], 13, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_NeverStopsTheOtherDrive()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var otherHandle = harness.Source.HandleFor('U');
        await otherHandle.Publish(WatchHarness.Batch(9, "before.txt", nextUsn: 150));
        Assert.AreEqual(150L, harness.BlockFor('U').Header.UsnNextUsn);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        await otherHandle.Publish(WatchHarness.Batch(10, "after.txt", nextUsn: 250));

        Assert.AreEqual(1, harness.Source.StartsFor('U').Count);
        Assert.AreEqual(0, otherHandle.DisposeCount);
        Assert.AreEqual(250L, harness.BlockFor('U').Header.UsnNextUsn);
        CollectionAssert.AreEqual(new[] { "before.txt", "after.txt" },
            harness.Changes.Select(change => change.Entry.Name).ToArray());
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task Rescan_OfT_LeavesUsPumpRunning()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var otherHandle = harness.Source.HandleFor('U');
        var producing = harness.HoldNextProduction('T');
        var rescan = harness.Index.RescanAsync('T', Token);
        await producing.Entered.WaitAsync(HangGuard);

        await otherHandle.Publish(WatchHarness.Batch(10, "u.txt", nextUsn: 555));

        Assert.IsFalse(rescan.IsCompleted, "T's producer is still gated");
        Assert.IsTrue(harness.Changes.Any(change => change.Entry.Name == "u.txt"), "U applied while T's scan ran");
        Assert.AreEqual(555L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.AreEqual(0, otherHandle.DisposeCount);
        producing.Release();
        await rescan.WaitAsync(HangGuard);
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task RescanAsync_OfAHealthyDrive_WhenTheOtherDriveFaultsMidScan_RestartsItAndLeavesTheOtherFaulted()
    {
        using var harness = new WatchHarness('T', 'U');
        var lostChannel = new IOException("U's channel was lost");
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        var production = harness.HoldNextProduction('T');
        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        var rescan = harness.Index.RescanAsync('T', Token);
        await production.Entered.WaitAsync(HangGuard);

        // T's watch is running and carries no failure, so U dropping mid-scan does not touch it.
        harness.Source.HandleFor('U').LoseChannel(lostChannel);
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'U');
        production.Release();
        await rescan.WaitAsync(HangGuard);

        Assert.AreEqual(2, harness.Source.StartsFor('T').Count);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.Source.StartsFor('T')[1]);
        Assert.AreEqual(1, harness.Source.StartsFor('U').Count);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, harness.DriveFor('T').WatchCatchUp);
        Assert.AreEqual("U's channel was lost", harness.DriveFor('U').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.DriveFor('U').WatchCatchUp);

        await harness.Source.HandleFor('T').Publish(
            new JournalBatch([WatchHarness.Create(9, "after.txt")], 13, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);
        var thrown = await ThrowsAsync<IOException>(() => harness.Index.StopWatchingAsync('U', Token));
        Assert.AreSame(lostChannel, thrown);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_DropsABatchThePumpHadAlreadyAcceptedWhenTheDriveWasRetired()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        var applying = harness.HoldFirstApply('T');
        var queued = harness.Source.HandleFor('T').Queue(WatchHarness.Batch(9, "stale.txt", nextUsn: 5000));
        await applying.Entered.WaitAsync(HangGuard);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        var rescan = harness.Index.RescanAsync('T', Token);
        Assert.IsFalse(rescan.IsCompleted, "the rescan waits for the retiring pump to drain");
        applying.Release();
        await rescan.WaitAsync(HangGuard);
        await queued.WaitAsync(HangGuard);

        // The pump had already read the batch, so no source-side rule can reach it: the scoping
        // rule drops it because its instance is no longer the drive's current one.
        Assert.AreEqual(0, harness.Changes.Count);
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalId);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task RescanAsync_AfterEveryDriveFaulted_RestartsOnlyTheRescannedDriveAndLeavesTheOthersFaulted()
    {
        using var harness = new WatchHarness('T', 'U');
        harness.Index.HoldEveryRecovery();
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);
        harness.Source.HandleFor('T').FailDrive(new IOException("T's journal wrapped"));
        harness.Source.HandleFor('U').FailDrive(new IOException("U's journal wrapped"));
        await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'T');
        var uFault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 'U');

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        var starts = harness.Source.StartsFor('T');
        Assert.AreEqual(2, starts.Count);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), starts[1]);
        Assert.AreEqual(1, harness.Source.StartsFor('U').Count, "U's own failure still stands, so it needs its own rescan.");
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);
        Assert.AreEqual(uFault.Exception.Message, harness.DriveFor('U').WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Recovering, harness.DriveFor('U').WatchCatchUp, "U's recovery is still held");

        await harness.Source.HandleFor('T').Publish(
            new JournalBatch([WatchHarness.Create(10, "t.txt")], 13, 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);

        await harness.Index.StopWatchingAsync('T', Token);
        var thrown = await ThrowsAsync<DriveWatchFaultException>(() => harness.Index.StopWatchingAsync('U', Token));
        Assert.AreSame(uFault.Exception, thrown);
    }

    [TestMethod]
    public async Task RescanAsync_WithNoWatchRunning_StartsNoWatch()
    {
        using var harness = new WatchHarness('T', 'U');

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(0, harness.Source.Starts.Count);
        var drive = harness.DriveFor('T');
        Assert.IsFalse(drive.WatchRequested);
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.WatchCatchUp);
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalId);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => harness.Index.WaitForCatchUpAsync('T', Token));
    }

    [TestMethod]
    public async Task RescanAsync_WhoseProducerThrowsCancellation_KeepsTheDriveWatchingAndRethrows()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync('T', Token);
        await harness.Index.StartWatchingAsync('U', Token);

        var scanFailure = new OperationCanceledException("the scan was cancelled");
        harness.FailNextProduction('T', scanFailure);
        var thrown = await ThrowsAsync<OperationCanceledException>(() => harness.Index.RescanAsync('T', Token));

        Assert.AreSame(scanFailure, thrown);

        // The block was never swapped, so the original watch keeps its cursor and catch-up.
        var starts = harness.Source.StartsFor('T');
        Assert.AreEqual(1, starts.Count);
        Assert.AreEqual(new IndexWatchTarget('T', WatchHarness.JournalId, WatchHarness.NextUsn), starts[0]);
        Assert.IsNull(harness.DriveFor('T').WatchFailureMessage);

        await harness.Source.HandleFor('T').Publish(WatchHarness.Batch(9, "after.txt", nextUsn: 5000));
        Assert.AreEqual(5000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync('T', Token);
        await harness.Index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledWhileAwaitingWriteGate_DisposesCompletedScanAndRestoresRetiredCache()
    {
        using var harness = new WatchHarness('T');

        await harness.Index.WaitForDriveWriteGateForTest('T');
        try
        {
            var initialBlock = harness.Index.Root('T').DriveBlock;
            var initialProducedBlock = harness.BlockFor('T');
            using var rescanCancellation = new CancellationTokenSource();
            harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);

            var rescan = harness.Index.RescanAsync('T', rescanCancellation.Token);
            var producedBlock = await WaitForReplacementBlockAsync(harness, 'T', initialProducedBlock, rescan);
            await rescanCancellation.CancelAsync();

            await ThrowsAsync<OperationCanceledException>(() => rescan.WaitAsync(HangGuard));

            Assert.ThrowsException<ObjectDisposedException>(() => _ = producedBlock.Header);
            Assert.AreSame(initialBlock, harness.Index.Root('T').DriveBlock);
            Assert.AreEqual(0, Directory.GetFiles(harness.CacheDirectory, "*.retired-*").Length);
        }
        finally
        {
            harness.Index.ReleaseDriveWriteGateForTest('T');
        }
    }

    /// <summary>
    ///     Two MFT drives over one temp tree: 'U' gets a valid block pre-written at its canonical
    ///     cache path (cursor 22/8484) so a cache-only open warm-starts it, and 'T' gets none, so
    ///     the same open declines it. The producer runs only on a rescan of 'T'.
    /// </summary>
    FileIndexOptions CacheOnlyWatchOptions(FakeIndexWatchSource source, bool failDriveT)
    {
        MftBlockFixture.Write(Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('U', 2)),
            volumeSerial: 2, journalId: 22, nextUsn: 8484, moment: MftBlockFixture.SeededMoment);
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1), new IndexedDrive('U', _treeRoot, 2)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, _cancellationToken) =>
            {
                if (failDriveT && char.ToUpperInvariant(request.DriveLetter) == 'T')
                {
                    throw new UnauthorizedAccessException("elevation declined");
                }

                MftBlockFixture.Write(request.BlockPath, request.VolumeSerial, journalId: 7, nextUsn: 4096, moment: MftBlockFixture.SeededMoment);
                return Task.FromResult(new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
            },
            WatchSource = source,
            InitialOpenCacheOnly = true
        };
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDrive_AdoptsItWithoutAWatchAndALaterStartAppliesItsBatches()
    {
        var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        var declined = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, declined.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, declined.FailureKind);
        await index.StartWatchingAsync('U', Token);

        await index.RescanAsync('T', Token);

        // 'T' never asked to be watched, so its adoption starts nothing.
        Assert.AreEqual(0, source.StartsFor('T').Count);
        var recovered = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Ready, recovered.State);
        Assert.AreEqual(DriveFailureKind.None, recovered.FailureKind);
        Assert.AreEqual(WatchCatchUpState.NotStarted, recovered.WatchCatchUp);

        await index.StartWatchingAsync('T', Token);
        Assert.AreEqual(new IndexWatchTarget('T', 7, 4096), source.StartsFor('T').Single());
        Assert.AreEqual(WatchCatchUpState.CatchingUp,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);

        await source.HandleFor('T').Publish(new JournalBatch([WatchHarness.Create(9, "after.txt")], 7, 5000));
        Assert.AreEqual(5000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        var caughtUp = index.WaitForCatchUpAsync('T', Token);
        await source.HandleFor('T').Publish(new DriveCaughtUp());
        await caughtUp.WaitAsync(HangGuard);
        Assert.AreEqual(WatchCatchUpState.CaughtUp,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
        await index.StopWatchingAsync('T', Token);
        await index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_WhoseScanFails_StaysFailedAndKeepsTheWatchUntouched()
    {
        var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: true), Token);
        await index.StartWatchingAsync('U', Token);

        var thrown = await ThrowsAsync<InvalidOperationException>(() => index.RescanAsync('T', Token));

        StringAssert.Contains(thrown.Message, "elevation declined");
        var status = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.FailureKind);
        Assert.AreEqual("elevation declined", status.MftProducerFailureMessage);
        Assert.AreEqual(0, source.StartsFor('T').Count, "a still-blockless drive is never watched");

        // The other drive never noticed.
        Assert.AreEqual(0, source.HandleFor('U').DisposeCount);
        await source.HandleFor('U').Publish(new JournalBatch([WatchHarness.Create(9, "u.txt")], 22, 8500));
        Assert.AreEqual(8500L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);
        await index.StopWatchingAsync('U', Token);
        await ThrowsAsync<InvalidOperationException>(() => index.StopWatchingAsync('T', Token));
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_RescannedASecondTime_RestartsIt()
    {
        var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync('U', Token);
        await index.RescanAsync('T', Token);
        await index.StartWatchingAsync('T', Token);
        var firstHandle = source.HandleFor('T');

        // 'T' is now watching, so the rescan retires its watch at publication and starts it
        // again afterwards.
        await index.RescanAsync('T', Token);

        Assert.AreEqual(2, source.StartsFor('T').Count);
        Assert.AreEqual(1, firstHandle.DisposeCount);
        await source.HandleFor('T').Publish(new JournalBatch([WatchHarness.Create(10, "second.txt")], 7, 6000));
        Assert.AreEqual(6000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        await index.StopWatchingAsync('T', Token);
        await index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_WhenOnlyInitialDriveFaults_KeepsWatchingAdoptedDrive()
    {
        var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        index.HoldEveryRecovery();
        await index.StartWatchingAsync('U', Token);
        await index.RescanAsync('T', Token);
        await index.StartWatchingAsync('T', Token);

        var faulted = NextFaultOf(index, 'U');
        source.HandleFor('U').FailDrive(new IOException("U journal error"));
        var fault = await faulted.WaitAsync(HangGuard);
        Assert.IsNotNull(index.Drives.Single(drive => drive.DriveLetter == 'U').WatchFailureMessage);

        await source.HandleFor('T').Publish(new JournalBatch([WatchHarness.Create(9, "surviving.txt")], 7, 5500));
        Assert.AreEqual(5500L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.IsNull(index.Drives.Single(drive => drive.DriveLetter == 'T').WatchFailureMessage);

        var thrown = await ThrowsAsync<DriveWatchFaultException>(() => index.StopWatchingAsync('U', Token));
        Assert.AreSame(fault.Exception, thrown);
        await index.StopWatchingAsync('T', Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_StreamEndsWithoutStop_FaultsAdoptedDriveCatchUp()
    {
        var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync('U', Token);
        await index.RescanAsync('T', Token);
        await index.StartWatchingAsync('T', Token);

        var catchUpTask = index.WaitForCatchUpAsync('T', Token);
        source.HandleFor('T').End();

        var thrown = await ThrowsAsync<InvalidOperationException>(() => catchUpTask.WaitAsync(HangGuard));
        StringAssert.Contains(thrown.Message, "ended without being stopped");
        Assert.AreEqual(WatchCatchUpState.Faulted,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_UnhandledSourceException_FaultsAdoptedDriveCatchUp()
    {
        var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync('U', Token);
        await index.RescanAsync('T', Token);
        await index.StartWatchingAsync('T', Token);

        var catchUpTask = index.WaitForCatchUpAsync('T', Token);
        var sourceException = new InvalidOperationException("stream crashed");
        source.HandleFor('T').LoseChannel(sourceException);

        var thrown = await ThrowsAsync<InvalidOperationException>(() => catchUpTask.WaitAsync(HangGuard));
        Assert.AreSame(sourceException, thrown);
        Assert.AreEqual(WatchCatchUpState.Faulted,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
    }

    /// <summary>
    ///     Polls until the producer has handed back a block other than <paramref name="original" />
    ///     for the drive, which is the moment the rescan holds a finished, unpublished scan.
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

    /// <summary>Completes with the first fault the index raises for the drive after this call.</summary>
    static Task<WatchFault> NextFaultOf(FileIndex index, char driveLetter)
    {
        var completion = new TaskCompletionSource<WatchFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.DriveLetter == driveLetter)
            {
                completion.TrySetResult(fault);
            }
        };
        return completion.Task;
    }

}
