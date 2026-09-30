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
///     a drive and says why, recording the watch as requested the way a start whose source threw
///     does. A successful <see cref="FileIndex.RescanAsync(char, CancellationToken)" /> writes a
///     fresh cursor, clears the refusal, and starts the requested watch. The journal
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
        MftBlockProduceRequest request, CancellationToken cancellationToken) =>
        MftBlockFixture.Produce(request, CachedJournalId, CachedNextUsn, FixedMoment);

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
    ///     The refusal leaves the watch requested, so a stop or a rescan applies afterwards.
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
    ///     A refusal leaves the watch requested, so the rescan that replaces the block clears the
    ///     refusal and starts the watch from the fresh cursor, exactly as it would after a start
    ///     whose source threw.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDrive_ClearsTheRefusalAndStartsTheRequestedWatch()
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
        Assert.IsTrue(index.Drives.Single(drive => drive.DriveLetter == 'T').WatchRequested,
            "a refused start records the request, as a start whose source threw does");

        await index.RescanAsync('T', Token);

        Assert.AreEqual(new IndexWatchTarget('T', CachedJournalId, CachedNextUsn), source.StartsFor('T').Single(),
            "the rescan starts the requested watch from the fresh cursor");
        var recovered = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(BlockSource.ProducedByScan, recovered.BlockSource);
        Assert.IsNull(recovered.CheckpointLoss, "the rescan replaced the block the report described");
        Assert.IsNull(recovered.WatchFailureMessage);
        Assert.IsTrue(recovered.WatchRequested);
        Assert.AreEqual(WatchCatchUpState.CatchingUp,
            index.Drives.Single(drive => drive.DriveLetter == 'T').WatchCatchUp);
        await source.HandleFor('T').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalId: CachedJournalId, NextUsn: 5_000));
        Assert.AreEqual(5_000L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);

        await index.StopWatchingAsync('T', Token);
        await index.StopWatchingAsync('U', Token);
    }

    /// <summary>
    ///     A rescan whose producer returns no block replaces nothing and throws
    ///     <see cref="InvalidOperationException" /> carrying the producer's failure, and the drive's
    ///     refusal survives exactly as it was: the old, still-unresumable block is not started, and
    ///     the message explaining why is not cleared.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableDrive_WhenTheProducerReturnsNoBlock_ThrowsAndLeavesTheRefusalIntact()
    {
        var driveT = Drive('T', _firstTreeRoot);
        var driveU = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(driveT, driveU);

        var scanFailure = new IOException("synthetic scan failure for T");
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            // The producer throws, and the scanning code turns that into a scan with no block.
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

        var thrown = await FileIndexWatchRescanTests.ThrowsAsync<InvalidOperationException>(
            () => index.RescanAsync('T', Token));

        Assert.AreSame(scanFailure, thrown.InnerException);
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

    /// <summary>
    ///     An enumeration policy warm-starts a cached MFT block, which can be watched, but its
    ///     rescan produces an enumeration block, which cannot. The restart withdraws the request
    ///     and faults a wait issued between the retired watch and that restart.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ThatReplacesAWatchedBlockWithAnUnwatchableOne_WithdrawsTheRequestAndFaultsTheWait()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);
        var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Healthy });
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [driveT],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            WatchSource = source
        }, Token);
        try
        {
            Assert.AreEqual(ProducerKind.Mft, index.Drives.Single().ProducerKind, "the cached MFT block warm-started");
            await index.StartWatchingAsync('T', Token);
            Task? wait = null;
            index.BeforeRestartDecisionForTest = _ => wait ??= index.WaitForCatchUpAsync('T', CancellationToken.None);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => index.RescanAsync('T', Token));

            Assert.IsNotNull(wait);
            var failure = await FileIndexWatchRescanTests.ThrowsAsync<InvalidOperationException>(
                () => wait.WaitAsync(FakeIndexWatchSource.HangGuard));
            StringAssert.Contains(failure.Message, "no MFT-backed block");
            var status = index.Drives.Single();
            Assert.AreEqual(ProducerKind.Enumeration, status.ProducerKind);
            Assert.IsFalse(status.WatchRequested);
            Assert.AreEqual(WatchCatchUpState.NotStarted, status.WatchCatchUp);
            Assert.AreEqual(1, source.StartsFor('T').Count);
        }
        finally
        {
            // Disposed explicitly, after the restart seam that captures the index can no longer run.
            await index.DisposeAsync();
        }
    }

    /// <summary>
    ///     The same rescan over a watch that had already faulted on its channel, which the rescan
    ///     retains until the restart: the restart supersedes that faulted watch with its failure,
    ///     so the drive reads exactly as it does after a healthy watch.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ThatReplacesAChannelFaultedWatchsBlockWithAnUnwatchableOne_ReadsNotStarted()
    {
        var driveT = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(driveT);
        var source = new FakeIndexWatchSource();
        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Healthy });
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [driveT],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            WatchSource = source
        }, Token);
        var channelFaulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Channel)
            {
                channelFaulted.TrySetResult();
            }
        };
        await index.StartWatchingAsync('T', Token);
        source.HandleFor('T').LoseChannel(new IOException("the channel went away"));
        await channelFaulted.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(WatchCatchUpState.Faulted, index.Drives.Single().WatchCatchUp);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => index.RescanAsync('T', Token));

        var status = index.Drives.Single();
        Assert.AreEqual(ProducerKind.Enumeration, status.ProducerKind);
        Assert.IsFalse(status.WatchRequested);
        Assert.AreEqual(WatchCatchUpState.NotStarted, status.WatchCatchUp);
        Assert.IsNull(status.WatchFailureMessage, "the superseded watch's channel failure no longer describes the drive");
        var notWatched = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.WaitForCatchUpAsync('T', Token));
        StringAssert.Contains(notWatched.Message, "is not being watched");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => index.StopWatchingAsync('T', Token));
        Assert.AreEqual(1, source.StartsFor('T').Count);
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

        // The refusal records the request, so a stop withdraws it without rethrowing the refusal,
        // which the start already threw; after that there is nothing left to stop.
        Assert.IsTrue(status.WatchRequested);
        await index.StopWatchingAsync('T', Token);
        var stopped = index.Drives.Single();
        Assert.IsFalse(stopped.WatchRequested);
        Assert.AreEqual(WatchCatchUpState.NotStarted, stopped.WatchCatchUp);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StopWatchingAsync('T', Token));
    }
}
