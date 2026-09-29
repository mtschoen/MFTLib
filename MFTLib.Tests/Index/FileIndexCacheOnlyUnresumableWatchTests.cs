using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A cache-only open adopts a block despite a lost journal checkpoint rather than failing
///     the drive (see <see cref="FileIndexCheckpointLossLifetimeTests" />), because a cache-only
///     open never watches and the block is still a correct snapshot as of its age. But starting a
///     live watch from that block's cursor would resume from a position the journal no longer
///     holds, so <see cref="FileIndex.StartWatchingAsync(char, CancellationToken)" /> refuses such
///     a drive, says why, and requests no watch. A successful <see cref="FileIndex.RescanAsync" />
///     writes a fresh cursor and clears the refusal, leaving the drive ready to start. The journal
///     read is swapped out through <c>JournalCheckpointCheck.OverrideJournalForTest</c>, so these
///     run on every platform and never touch a real volume.
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

    /// <summary>Writes the drives' cache blocks while the journal still holds every checkpoint.</summary>
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


    /// <summary>
    ///     Starts a drive whose block is unresumable and returns the refusal the consumer sees.
    ///     A refusal requests no watch, so the drive is not watching afterwards.
    /// </summary>
    static async Task<InvalidOperationException> RefuseStartAsync(FileIndex index, char driveLetter,
        CancellationToken token)
    {
        var refusal = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StartWatchingAsync(driveLetter, token));
        StringAssert.Contains(refusal.Message, "RescanAsync");
        return refusal;
    }

    [TestMethod]
    public async Task StartWatchingAsync_RefusesTheUnresumableDriveAndWatchesTheHealthyOne()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source, cacheOnly: true, driveT, driveU), Token);

        // Both drives are adopted (cache-only never fails a drive for a lost checkpoint alone).
        Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.DriveLetter == 'T').State);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single(drive => drive.DriveLetter == 'U').State);
        Assert.IsNotNull(index.Drives.Single(drive => drive.DriveLetter == 'T').CheckpointLoss);
        Assert.IsNull(index.Drives.Single(drive => drive.DriveLetter == 'U').CheckpointLoss);

        await RefuseStartAsync(index, 'T', Token);
        await index.StartWatchingAsync('U', Token);

        Assert.AreEqual('U', source.Starts.Single().DriveLetter,
            "the drive whose checkpoint could not be resumed must never reach the watch source");

        var unresumable = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.IsNotNull(unresumable.WatchFailureMessage, "the refusal must be reported, not silent");
        StringAssert.Contains(unresumable.WatchFailureMessage, "RescanAsync");
        Assert.AreEqual(WatchCatchUpState.Faulted, unresumable.WatchCatchUp);

        var healthy = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.IsNull(healthy.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.CatchingUp, healthy.WatchCatchUp);

        await source.HandleFor('U').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: CachedJournalId, NextUsn: 9_500));
        Assert.AreEqual(9_500L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync('U', Token);
    }

    /// <summary>
    ///     A refusal requests no watch, so the rescan that replaces the block clears the
    ///     refusal and leaves the drive ready to start; it does not start the drive itself.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDrive_ClearsTheRefusalAndLeavesTheDriveReadyToStart()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source, cacheOnly: true, driveT, driveU), Token);
        await index.StartWatchingAsync('U', Token);
        await RefuseStartAsync(index, 'T', Token);

        await index.RescanAsync('T', Token);

        Assert.AreEqual(0, source.StartsFor('T').Count,
            "a refused drive requested no watch, so the rescan starts none");
        var recovered = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(BlockSource.ProducedByScan, recovered.BlockSource);
        Assert.IsNull(recovered.CheckpointLoss, "the rescan replaced the block the report described");
        Assert.IsNull(recovered.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.NotStarted, recovered.WatchCatchUp);

        await index.StartWatchingAsync('T', Token);

        Assert.AreEqual(new IndexWatchTarget('T', CachedJournalId, CachedNextUsn), source.StartsFor('T').Single());
        Assert.AreEqual(WatchCatchUpState.CatchingUp,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
        await source.HandleFor('T').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: CachedJournalId, NextUsn: 5_000));
        Assert.AreEqual(5_000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync('T', Token);
        await index.StopWatchingAsync('U', Token);
    }

    /// <summary>
    ///     Review finding 1 (PR 230, round 1): a rescan whose producer fails without throwing
    ///     replaces nothing, and the drive's refusal must survive that no-op exactly as it was:
    ///     the old, still-unresumable block must not be started, and the message explaining why
    ///     must not be cleared.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDrive_WhenTheScanFailsWithoutThrowing_LeavesTheRefusalIntact()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var scanFailure = new IOException("synthetic scan failure for T");
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            // The producer throws, but the scanning code catches that and returns no block: this
            // is the "non-throwing" failure the finding describes, since neither the swap nor
            // RescanAsync ever sees an exception for it.
            return char.ToUpperInvariant(request.DriveLetter) == 'T'
                ? throw scanFailure
                : ProduceMftShapedBlock(request, token);
        }

        var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed, ['U'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(Producer, source, cacheOnly: true, driveT, driveU), Token);
        await index.StartWatchingAsync('U', Token);
        await RefuseStartAsync(index, 'T', Token);

        var before = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.IsNotNull(before.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, before.WatchCatchUp);
        Assert.IsNotNull(before.CheckpointLoss);

        await index.RescanAsync('T', Token);

        Assert.AreEqual(0, source.StartsFor('T').Count,
            "a failed rescan must not start the cursor the journal still cannot resume");

        var after = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual("synthetic scan failure for T", after.MftProducerFailureMessage);
        Assert.IsNotNull(after.WatchFailureMessage, "the refusal must stay reported after a failed scan");
        StringAssert.Contains(after.WatchFailureMessage, "RescanAsync");
        Assert.AreEqual(WatchCatchUpState.Faulted, after.WatchCatchUp);
        Assert.IsNotNull(after.CheckpointLoss, "the block did not change, so the original loss still explains it");
        Assert.AreEqual(before.CheckpointLoss, after.CheckpointLoss);

        // U is unaffected by T's failed rescan.
        await source.HandleFor('U').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "u.txt")], JournalId: CachedJournalId, NextUsn: 9_000));
        Assert.AreEqual(9_000L, index.Root('U').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync('U', Token);
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheOnlyMftDriveIsUnresumable_RefusesItAndReportsIt()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);

        var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, source, cacheOnly: true, driveT), Token);

        var refusal = await RefuseStartAsync(index, 'T', Token);

        Assert.AreEqual(0, source.Starts.Count, "no drive survived to reach the watch source");
        var status = index.Drives.Single();
        Assert.AreEqual(refusal.Message, status.WatchFailureMessage);
        Assert.AreEqual(WatchCatchUpState.Faulted, status.WatchCatchUp);

        // The refusal is the drive's last start failure, so a catch-up wait faults with it.
        var waitFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.WaitForCatchUpAsync('T', Token));
        StringAssert.Contains(waitFailure.Message, "RescanAsync");

        // A refusal requests no watch, so there is nothing to stop.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StopWatchingAsync('T', Token));
    }
}
