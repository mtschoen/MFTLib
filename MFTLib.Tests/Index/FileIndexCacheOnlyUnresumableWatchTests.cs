using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A cache-only open adopts a block despite a lost journal checkpoint rather than failing
///     the drive (see <see cref="FileIndexCheckpointLossLifetimeTests" />), because a cache-only
///     open never watches and the block is still a correct snapshot as of its age. But arming a
///     live watch from that block's cursor would resume from a position the journal no longer
///     holds, so <see cref="FileIndex.StartWatchingAsync" /> must leave such a drive out of the
///     watch rather than silently resuming it wrong, and must say why. A successful
///     <see cref="FileIndex.RescanAsync" /> writes a fresh cursor and clears the refusal, arming
///     the drive onto a session already running exactly as an ordinary cache-declined drive does.
///     The journal read is swapped out through
///     <c>JournalCheckpointCheck._journalOverride</c>, so these run on every platform and never
///     touch a real volume.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCacheOnlyUnresumableWatchTests
{
    const ulong CachedJournalId = 0xABCD;
    const long CachedNextUsn = 1_000_000;
    static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    string _firstTreeRoot = null!;
    string _secondTreeRoot = null!;
    string _cacheDirectory = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestInitialize]
    public void Initialize()
    {
        _firstTreeRoot = NewDirectory();
        _secondTreeRoot = NewDirectory();
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
    }

    static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "Documents"));
        File.WriteAllText(Path.Combine(path, "Documents", "readme.md"), "hello");
        return path;
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _firstTreeRoot, _secondTreeRoot, _cacheDirectory })
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

    static IndexedDrive Drive(char letter, string root) =>
        new(letter, root, letter == 'T' ? 0x0BADF00Du : 0x0BADBEEFu);

    FileIndexOptions Options(MftBlockProducer producer, IIndexWatchSource? watchSource, bool cacheOnly,
        params IndexedDrive[] drives)
    {
        return new FileIndexOptions
        {
            Drives = drives,
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = producer,
            WatchSource = watchSource,
            InitialOpenCacheOnly = cacheOnly
        };
    }

    /// <summary>An MFT-kind block carrying the checkpoint a warm start would resume from.</summary>
    static Task<MftBlockProduceResult> ProduceMftShapedBlock(
        MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(CachedJournalId, CachedNextUsn);
            writer.Complete(FixedMoment);
        }

        return Task.FromResult(new MftBlockProduceResult(
            BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
            CachedJournalId, CachedNextUsn, SkippedRecordCount: 0, CompactionNeeded: false));
    }

    /// <summary>Answers each drive with its own journal, so one can be lost and another kept.</summary>
    static IDisposable Journals(Dictionary<char, JournalWindow> byDrive)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(
            drive => byDrive.TryGetValue(char.ToUpperInvariant(drive), out var window) ? window : null);
    }

    static JournalWindow Healthy => new(CachedJournalId, 0, CachedNextUsn, 64, 128L * 1024 * 1024);

    static JournalWindow Trimmed =>
        new(CachedJournalId, CachedNextUsn + 500, CachedNextUsn + 4_000, 64, 128L * 1024 * 1024);

    /// <summary>A fresh, resumable cursor distinct from the adopted block's, for a rescan's block.</summary>
    static Task<MftBlockProduceResult> ProduceRescannedMftShapedBlock(
        MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const ulong freshJournalId = 99;
        const long freshNextUsn = 6_000;
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(freshJournalId, freshNextUsn);
            writer.Complete(FixedMoment);
        }

        return Task.FromResult(new MftBlockProduceResult(
            BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
            freshJournalId, freshNextUsn, SkippedRecordCount: 0, CompactionNeeded: false));
    }

    /// <summary>Writes both drives' cache blocks while the journal still holds both checkpoints.</summary>
    async Task SeedCacheAsync(params IndexedDrive[] drives)
    {
        using var journals = Journals(drives.ToDictionary(drive => drive.DriveLetter, _ => Healthy));
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, watchSource: null, cacheOnly: false, drives), Token);
        foreach (var drive in index.Drives)
        {
            Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
        }
    }

    [TestMethod]
    public async Task StartWatchingAsync_LeavesTheUnresumableDriveOutAndWatchesTheHealthyOne()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        using var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source, cacheOnly: true, driveT, driveU), Token);

        // Both drives are adopted (cache-only never fails a drive for a lost checkpoint alone).
        Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.DriveLetter == 'T').State);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.DriveLetter == 'U').State);
        Assert.IsNotNull(index.Drives.Single(drive => drive.DriveLetter == 'T').CheckpointLoss);
        Assert.IsNull(index.Drives.Single(drive => drive.DriveLetter == 'U').CheckpointLoss);

        await index.StartWatchingAsync(Token);
        var targets = await source.SourceStartedAsync();

        Assert.AreEqual('U', targets.Single().DriveLetter,
            "the drive whose checkpoint could not be resumed must never reach the watch source");

        var unresumable = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.IsNotNull(unresumable.WatchFailureMessage, "the refusal must be reported, not silent");
        StringAssert.Contains(unresumable.WatchFailureMessage, "RescanAsync");
        Assert.AreEqual(WatchCatchUpState.Faulted, unresumable.WatchCatchUp);

        var healthy = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.IsNull(healthy.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, healthy.WatchCatchUp);

        await source.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: CachedJournalId, NextUsn: 9_500));
        Assert.AreEqual(9_500L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDriveWhileWatching_ClearsTheRefusalAndArmsItOntoTheRunningWatch()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        using var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source, cacheOnly: true, driveT, driveU), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        // The rescan's own scan runs under a journal that would once again mark 'T' healthy, so
        // the fresh block it produces carries a resumable cursor.
        await index.RescanAsync('T', Token);

        // 'T' was never on the stream, so there is nothing to disarm; the resume arms it once
        // its scan produced a block with a fresh cursor, exactly as an ordinary cache-declined
        // drive's rescan does.
        CollectionAssert.AreEqual(new[] { "arm:T" }, source.WatchOperations.ToArray());

        var recovered = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(BlockSource.ProducedByScan, recovered.BlockSource);
        Assert.IsNull(recovered.CheckpointLoss, "the rescan replaced the block the report described");
        Assert.IsNull(recovered.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, recovered.WatchCatchUp);

        await source.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: CachedJournalId, NextUsn: 5_000));
        Assert.AreEqual(5_000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(Token);
    }

    /// <summary>
    ///     Review finding 1 (PR 230, round 1): <c>ProduceRescannedBlockAsync</c> can return null
    ///     when the MFT producer fails without throwing, and <c>SwapDriveBlockAsync</c> then
    ///     returns without replacing the block or clearing the unresumable marker. The drive's
    ///     refusal must survive that no-op exactly as it was: the old, still-unresumable block
    ///     must not be armed onto the running session, and the message explaining why must not be
    ///     cleared.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDriveWhileWatching_WhenTheScanFailsWithoutThrowing_LeavesTheRefusalIntact()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var scanFailure = new IOException("synthetic scan failure for T");
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            // The producer throws, but FileIndex.Scanning.cs's ProduceDriveBlockAsync catches
            // that and returns null: this is the "non-throwing" failure the finding describes,
            // since neither SwapDriveBlockAsync nor RescanAsync ever sees an exception for it.
            return char.ToUpperInvariant(request.DriveLetter) == 'T'
                ? throw scanFailure
                : ProduceMftShapedBlock(request, token);
        }

        using var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(Producer, source, cacheOnly: true, driveT, driveU), Token);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        var before = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.IsNotNull(before.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, before.WatchCatchUp);
        Assert.IsNotNull(before.CheckpointLoss);

        await index.RescanAsync('T', Token);

        CollectionAssert.DoesNotContain(source.WatchOperations.ToArray(), "arm:T",
            "a failed rescan must not arm the cursor the journal still cannot resume");

        var after = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual("synthetic scan failure for T", after.MftProducerFailureMessage);
        Assert.IsNotNull(after.WatchFailureMessage, "the refusal must stay reported after a failed scan");
        StringAssert.Contains(after.WatchFailureMessage, "RescanAsync");
        Assert.AreEqual(WatchCatchUpState.Faulted, after.WatchCatchUp);
        Assert.IsNotNull(after.CheckpointLoss, "the block did not change, so the original loss still explains it");
        Assert.AreEqual(before.CheckpointLoss, after.CheckpointLoss);

        // 'U' is unaffected by 'T's failed rescan.
        await source.PublishAsync(new JournalBatch('U',
            [WatchHarness.Create(recordNumber: 9, "u.txt")], JournalId: CachedJournalId, NextUsn: 9_000));
        Assert.AreEqual(9_000L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(Token);
    }

    /// <summary>
    ///     Review finding 2 (PR 230, round 1): <c>SuspendDriveForRescanAsync</c> captures the
    ///     watch session (or its absence) before the scan runs. If no session existed at that
    ///     moment but one starts while the scan is still in flight, that new session excludes the
    ///     rescanning drive (its old, unresumable block is still the one on record) and the stale
    ///     "no session" capture then makes the resume skip registering the drive once its scan
    ///     succeeds, even though a session is now running. The interleaving is driven from state,
    ///     through <see cref="TaskCompletionSource" /> gates the test owns, never from a sleep.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_StartedBeforeAnyWatchSession_StillArmsTheRecoveredDriveOntoASessionThatStartedMidScan()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var producerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            return char.ToUpperInvariant(request.DriveLetter) == 'T'
                ? ProduceAfterReleaseAsync(request, token)
                : ProduceMftShapedBlock(request, token);
        }

        async Task<MftBlockProduceResult> ProduceAfterReleaseAsync(MftBlockProduceRequest request,
            CancellationToken token)
        {
            producerEntered.TrySetResult();
            await releaseProducer.Task.ConfigureAwait(false);
            return await ProduceRescannedMftShapedBlock(request, token).ConfigureAwait(false);
        }

        using var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(Producer, source, cacheOnly: true, driveT, driveU), Token);

        // No watch session exists yet: RescanAsync captures a null session at suspend time.
        var rescanTask = index.RescanAsync('T', Token);
        await producerEntered.Task.WaitAsync(FakeIndexWatchSource.HangGuard);

        // A session starts while 'T's scan is still in flight. 'T's block on record is still
        // the old, unresumable one, so only 'U' reaches the watch source.
        await index.StartWatchingAsync(Token);
        var initialTargets = await source.SourceStartedAsync();
        Assert.AreEqual('U', initialTargets.Single().DriveLetter);

        releaseProducer.SetResult();
        await rescanTask;

        // 'T's scan produced a fresh, resumable cursor. It must join the session that started
        // mid-scan rather than being silently left off it.
        Assert.IsTrue(source.WatchOperations.Contains("arm:T"),
            "a drive that only became watchable mid-rescan must still be armed onto the running session");

        var recovered = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.IsNull(recovered.CheckpointLoss);
        Assert.IsNull(recovered.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, recovered.WatchCatchUp);

        await source.PublishAsync(new JournalBatch('T',
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: 99, NextUsn: 6_500));
        Assert.AreEqual(6_500L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync(Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenEveryMftDriveIsUnresumable_StartsNoSessionButReportsEachDrive()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);

        using var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source, cacheOnly: true, driveT), Token);

        await index.StartWatchingAsync(Token);

        Assert.AreEqual(0, source.SourceInvocationCount, "no target survived to reach the watch source");
        var status = index.Drives.Single();
        Assert.IsNotNull(status.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, status.WatchCatchUp);

        // No session was ever created, so waiting for a catch-up on this drive is refused the
        // same way it would be for a drive that was never armed at all.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.WaitForCatchUpAsync('T', Token));

        // Stopping a watch that never started is a no-op, matching every other index with
        // nothing watchable.
        await index.StopWatchingAsync(Token);
    }
}
