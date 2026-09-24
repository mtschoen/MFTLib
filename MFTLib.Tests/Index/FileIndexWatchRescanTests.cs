using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The recovery contract: a rescan while a watch is running touches exactly one drive, and a
///     rescan after every drive has faulted reclaims the session and starts a fresh one.
/// </summary>
[TestClass]
public class FileIndexWatchRescanTests
{
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
    public async Task RescanAsync_WhileWatching_ReArmsOnlyTheRescannedDriveFromTheFreshCursorAndClearsItsFailure()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("journal wrapped")));
        Assert.IsNotNull(DriveFor(harness, 'T').WatchFailureMessage);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(1, harness.SourceInvocationCount);
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.ArmedDrives.Single());
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 13, NextUsn: 9500));
        Assert.AreEqual(9500L, harness.BlockFor('T').Header.UsnNextUsn);

        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_NeverStopsTheOtherDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var changes = new List<FileChange>();
        harness.Index.Changed += changes.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "before.txt")], JournalId: 22, NextUsn: 8500));
        Assert.AreEqual(8500L, harness.BlockFor('U').Header.UsnNextUsn);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "after.txt")], JournalId: 22, NextUsn: 8600));

        CollectionAssert.DoesNotContain(harness.DisarmedDrives.ToArray(), 'U');
        CollectionAssert.DoesNotContain(harness.ArmedDrives.Select(target => target.DriveLetter).ToArray(), 'U');
        Assert.AreEqual(8600L, harness.BlockFor('U').Header.UsnNextUsn);
        CollectionAssert.AreEqual(new[] { "before.txt", "after.txt" },
            changes.Select(change => change.Entry.Name).ToArray());
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhileWatching_DropsABatchAlreadyQueuedOnTheMergedStreamWhenTheDriveWasDisarmed()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        harness.HoldItemsUnread();
        var queued = harness.Queue(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "stale.txt")], JournalId: 11, NextUsn: 5000));

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        harness.ReleaseHeldItems();
        await queued.WaitAsync(FakeIndexWatchSource.HangGuard);

        // The wire arm epoch drops what the broker had already written; this drops what the
        // source had already queued, which is the one no wire rule can reach.
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalId);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_AfterEveryDriveFaulted_ReclaimsTheSessionAndStartsAFreshOne()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();
        await harness.PublishAsync(new DriveWatchFailure('T', new IOException("T's journal wrapped")));
        await harness.PublishAsync(new DriveWatchFailure('U', new IOException("U's journal wrapped")));
        await harness.SourceEndedAsync();

        // Fenced on pump completion so this pins the suspend step that finds the pump already
        // finished. The orderings where the session ends while the rescan is in flight are in
        // FileIndexWatchRescanEndedSessionTests.
        await harness.WaitForPumpToCompleteAsync();

        await harness.Index.RescanAsync('T', Token);
        var targets = await harness.SourceStartedAsync();

        Assert.AreEqual(2, harness.SourceInvocationCount);
        Assert.AreEqual(0, harness.WatchOperations.Count, "A dead session is restarted whole, not re-armed.");
        Assert.AreEqual('T', targets.Single().DriveLetter, "U's own failure still stands, so it needs its own rescan.");
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual("U's journal wrapped", DriveFor(harness, 'U').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 10, "t.txt")], JournalId: 11, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);

        // T's fault was recovered by the rescan; U's was carried into the fresh session.
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(() => harness.Index.StopWatchingAsync(Token));
        Assert.AreEqual("U's journal wrapped", thrown.Message);
    }

    [TestMethod]
    public async Task RescanAsync_WithNoWatchRunning_ArmsNothingAndDisarmsNothing()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(0, harness.SourceInvocationCount);
        Assert.AreEqual(0, harness.ArmedDrives.Count);
        Assert.AreEqual(0, harness.DisarmedDrives.Count);
        Assert.AreEqual(13ul, harness.BlockFor('T').Header.UsnJournalId);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseSwapFails_ReArmsTheDriveFromItsUnchangedCursorAndRethrows()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var scanFailure = new OperationCanceledException("the scan was cancelled");
        harness.FailNextProduction('T', scanFailure);
        var thrown = await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => harness.Index.RescanAsync('T', Token));

        Assert.AreSame(scanFailure, thrown);

        // The block was never swapped, so the drive resumes from the cursor its header still
        // carries. A failed rescan must not leave the drive disarmed and silently unwatched.
        CollectionAssert.AreEqual(new[] { "disarm:T", "arm:T" }, harness.WatchOperations.ToArray());
        Assert.AreEqual(new IndexWatchTarget('T', 11, 4242), harness.ArmedDrives.Single());
        Assert.IsNull(DriveFor(harness, 'T').WatchFailureMessage);

        await harness.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 11, NextUsn: 5000));
        Assert.AreEqual(5000L, harness.BlockFor('T').Header.UsnNextUsn);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseSwapAndReArmBothFail_AnnouncesTheFreezeAndLeavesTheOtherDriveStreaming()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var scanFailure = new OperationCanceledException("the scan was cancelled");
        harness.FailNextProduction('T', scanFailure);
        harness.FailNextArm(new IOException("the source could not resume this drive"));
        var thrown = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => harness.Index.RescanAsync('T', Token));

        // The freeze is made visible rather than left silent, and the original failure is what
        // names it: the re-arm failure is only why the drive could not be put back.
        CollectionAssert.Contains(thrown.InnerExceptions.ToArray(), scanFailure);
        Assert.AreEqual("the scan was cancelled", DriveFor(harness, 'T').WatchFailureMessage);
        Assert.AreEqual(DriveState.Ready, DriveFor(harness, 'T').State);
        var driveFault = faults.Single(fault => fault.DriveLetter == 'T');
        Assert.AreEqual(WatchFaultKind.Source, driveFault.Kind);
        Assert.AreSame(scanFailure, driveFault.Exception);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);

        // The rescan already threw this failure to its own caller, so the pump never observed it
        // and the stop has nothing to rethrow.
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseDisarmFails_AnnouncesTheStoppedDriveAndLeavesTheOtherStreaming()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        var disarmFailure = new IOException("the source could not stop this drive");
        harness.FailNextDisarm(disarmFailure);
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.RescanAsync('T', Token));

        // A disarm that throws has already taken the drive off the watch, so a healthy status and
        // no fault would be exactly the silent stop the swap path is written to prevent.
        Assert.AreSame(disarmFailure, thrown);
        Assert.AreEqual("the source could not stop this drive", DriveFor(harness, 'T').WatchFailureMessage);
        var driveFault = faults.Single(fault => fault.DriveLetter == 'T');
        Assert.AreEqual(WatchFaultKind.Source, driveFault.Kind);
        Assert.AreSame(disarmFailure, driveFault.Exception);

        await harness.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 10, "u.txt")], JournalId: 22, NextUsn: 9000));
        Assert.AreEqual(9000L, harness.BlockFor('U').Header.UsnNextUsn);
        Assert.IsNull(DriveFor(harness, 'U').WatchFailureMessage);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_WhoseReArmFailsAfterTheSwapSucceeded_AnnouncesTheStoppedDrive()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242), new IndexWatchTarget('U', 22, 8484)]);
        var faults = new List<WatchFault>();
        harness.Index.WatchFaulted += faults.Add;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        var armFailure = new IOException("the source could not resume this drive");
        harness.FailNextArm(armFailure);
        var thrown = await Assert.ThrowsExceptionAsync<IOException>(
            () => harness.Index.RescanAsync('T', Token));

        // The swap succeeded, so the block is current and only the watch is missing. Clearing the
        // failure before arming is what would otherwise leave the drive reading healthy.
        Assert.AreSame(armFailure, thrown);
        Assert.AreEqual(9000L, harness.BlockFor('T').Header.UsnNextUsn);
        Assert.AreEqual("the source could not resume this drive", DriveFor(harness, 'T').WatchFailureMessage);
        var driveFault = faults.Single(fault => fault.DriveLetter == 'T');
        Assert.AreEqual(WatchFaultKind.Source, driveFault.Kind);
        Assert.AreSame(armFailure, driveFault.Exception);
        await harness.Index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledWhileAwaitingSwapGate_DisposesCompletedScanAndRestoresRetiredCache()
    {
        using var harness = new WatchHarness(
            [new IndexWatchTarget('T', 11, 4242)]);

        var swapGateField = typeof(FileIndex).GetField("_swapGate",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var swapGate = (SemaphoreSlim)swapGateField.GetValue(harness.Index)!;

        await swapGate.WaitAsync(Token);
        try
        {
            var driveBlocksField = typeof(FileIndex).GetField("_driveBlocks",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var driveBlocks = (List<DriveBlock>)driveBlocksField.GetValue(harness.Index)!;
            var initialBlock = driveBlocks[0];

            using var rescanCts = new CancellationTokenSource();
            harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);

            var rescanTask = harness.Index.RescanAsync('T', rescanCts.Token);

            while (!harness.ProducedBlocks.ContainsKey('T') && !rescanTask.IsCompleted)
            {
                await Task.Delay(10, Token);
            }
            await Task.Delay(50, Token);

            rescanCts.Cancel();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => rescanTask);

            var producedBlock = harness.ProducedBlocks['T'];
            Assert.ThrowsException<ObjectDisposedException>(() => _ = producedBlock.Header);

            Assert.AreSame(initialBlock, driveBlocks[0]);

            var retiredFiles = Directory.GetFiles(harness.CacheDirectory, "*.retired-*");
            Assert.AreEqual(0, retiredFiles.Length);
        }
        finally
        {
            swapGate.Release();
        }
    }

    static DriveStatus DriveFor(WatchHarness harness, char driveLetter)
    {
        return harness.Index.Drives.Single(drive => drive.DriveLetter == char.ToUpperInvariant(driveLetter));
    }

    /// <summary>
    ///     A small valid MFT-shaped block written at <paramref name="path" /> with the journal
    ///     cursor stamped before completion, the same shape the producer-selection tests build.
    ///     Pre-seeds 'U''s cache so a cache-only open warm-starts it while 'T' is declined, and
    ///     builds the block a rescan of 'T' adopts.
    /// </summary>
    static void WriteMftShapedBlock(string path, uint volumeSerial, ulong journalId, long nextUsn)
    {
        var moment = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        var createOptions = new BlockFileCreateOptions
        {
            Path = path,
            VolumeSerial = volumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256)
        };
        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(journalId, nextUsn);
            writer.Complete(moment);
        }
    }

    /// <summary>
    ///     Two MFT drives over one temp tree: 'U' gets a valid block pre-written at its canonical
    ///     cache path (cursor 22/8484) so a cache-only open warm-starts it, and 'T' gets none, so
    ///     the same open declines it. The producer runs only on a rescan of 'T'.
    /// </summary>
    FileIndexOptions CacheOnlyWatchOptions(FakeIndexWatchSource source, bool failDriveT)
    {
        WriteMftShapedBlock(Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('U', 2)),
            volumeSerial: 2, journalId: 22, nextUsn: 8484);
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

                WriteMftShapedBlock(request.BlockPath, request.VolumeSerial, journalId: 7, nextUsn: 4096);
                return Task.FromResult(new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
            },
            WatchSource = source,
            InitialOpenCacheOnly = true
        };
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_ArmsItWithoutADisarmAndAppliesItsBatches()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        var declined = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, declined.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, declined.FailureKind);

        await index.StartWatchingAsync(Token);
        var targets = await source.SourceStartedAsync();
        Assert.AreEqual('U', targets.Single().DriveLetter);

        await index.RescanAsync('T', Token);

        // 'T' was never on the stream, so there is nothing to disarm; the resume arms it once
        // its scan produced a block.
        CollectionAssert.AreEqual(new[] { "arm:T" }, source.WatchOperations.ToArray());
        var recovered = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Ready, recovered.State);
        Assert.AreEqual(DriveFailureKind.None, recovered.FailureKind);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, recovered.WatchCatchUp);

        await source.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 7, NextUsn: 5000));
        Assert.AreEqual(5000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await source.PublishAsync(new DriveCaughtUp('T'));
        await index.WaitForCatchUpAsync('T', Token);
        Assert.AreEqual(WatchCatchUpState.CaughtUp,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_WhoseScanFails_StaysFailedAndKeepsTheWatchUntouched()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: true), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        await index.RescanAsync('T', Token);

        var status = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.FailureKind);
        Assert.AreEqual("elevation declined", status.MftProducerFailureMessage);
        Assert.AreEqual(0, source.WatchOperations.Count,
            "a still-blockless drive was never disarmed and must not be armed either");

        // The other drive never noticed.
        await source.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "u.txt")], JournalId: 22, NextUsn: 8500));
        Assert.AreEqual(8500L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);
        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_RescannedASecondTime_DisarmsAndRearmsIt()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        // First rescan: adopts and arms 'T' without a disarm (it had no block).
        await index.RescanAsync('T', Token);
        CollectionAssert.AreEqual(new[] { "arm:T" }, source.WatchOperations.ToArray());

        // Second rescan: 'T' is now part of the watch session, so it must be disarmed before
        // scanning and re-armed afterwards.
        await index.RescanAsync('T', Token);
        CollectionAssert.AreEqual(new[] { "arm:T", "disarm:T", "arm:T" }, source.WatchOperations.ToArray());

        await source.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 10, "second.txt")], JournalId: 7, NextUsn: 6000));
        Assert.AreEqual(6000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_WhenOnlyInitialDriveFaults_KeepsWatchingAdoptedDrive()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        await index.RescanAsync('T', Token);

        // 'U' was the only initial target. Dropping it must not terminate the watch pump
        // because the adopted drive 'T' remains watched and healthy.
        await source.PublishAsync(new DriveWatchFailure('U', new IOException("U journal error")));
        Assert.IsNotNull(index.Drives.Single(drive => drive.DriveLetter == 'U').WatchFailureMessage);

        await source.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "surviving.txt")], JournalId: 7, NextUsn: 5500));
        Assert.AreEqual(5500L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.IsNull(index.Drives.Single(drive => drive.DriveLetter == 'T').WatchFailureMessage);

        await Assert.ThrowsExceptionAsync<IOException>(() => index.StopWatchingAsync(Token));
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_StreamEndsWithoutStop_FaultsAdoptedDriveCatchUp()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        await index.RescanAsync('T', Token);

        var catchUpTask = index.WaitForCatchUpAsync('T', Token);

        await source.CompleteSourceAsync();

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => catchUpTask);
        StringAssert.Contains(thrown.Message, "ended its stream without being stopped");
        Assert.AreEqual(WatchCatchUpState.Faulted,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_AggregateWaitForCatchUp_WaitsForAdoptedDrive()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        // Initial target 'U' catches up immediately.
        await source.PublishAsync(new DriveCaughtUp('U'));

        await index.RescanAsync('T', Token);

        // Aggregate wait must include the adopted drive 'T' rather than completing prematurely.
        var aggregateWait = index.WaitForCatchUpAsync(Token);
        Assert.IsFalse(aggregateWait.IsCompleted);

        await source.PublishAsync(new DriveCaughtUp('T'));
        await aggregateWait.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.IsTrue(aggregateWait.IsCompletedSuccessfully);

        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhileWatching_UnhandledSourceException_FaultsAdoptedDriveCatchUp()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await FileIndex.OpenAsync(CacheOnlyWatchOptions(source, failDriveT: false), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        await index.RescanAsync('T', Token);

        var catchUpTask = index.WaitForCatchUpAsync('T', Token);
        var sourceException = new InvalidOperationException("stream crashed");
        await source.FaultSourceAsync(sourceException);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => catchUpTask);
        Assert.AreSame(sourceException, thrown);
        Assert.AreEqual(WatchCatchUpState.Faulted,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
    }
}
