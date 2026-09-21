using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     What a consumer sees when a cached block cannot be resumed because the journal no
///     longer holds its checkpoint: the drive is cold-scanned, and its status carries the
///     reason and the size a journal would need to be at least to have kept the checkpoint.
///     The journal read is swapped out through <c>JournalCheckpointCheck._journalOverride</c>,
///     so these run on every platform and never touch a real volume.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCheckpointLossTests
{
    const ulong CachedJournalId = 0xABCD;
    const long CachedNextUsn = 1_000_000;
    static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
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

    FileIndexOptions Options()
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = ProduceMftShapedBlock
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

    static IDisposable Journal(ulong journalId, long firstUsn, long nextUsn, long allocationDelta = 64)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(
            _ => new JournalWindow(journalId, firstUsn, nextUsn, allocationDelta, 128L * 1024 * 1024));
    }

    /// <summary>Writes the cache block a later open will try to warm-start from.</summary>
    async Task SeedCacheAsync()
    {
        // The journal still holds the checkpoint while the cache is being written, so this
        // open is an ordinary cold scan and leaves a resumable block behind.
        using var journal = Journal(CachedJournalId, firstUsn: 0, nextUsn: CachedNextUsn);
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
        Assert.IsNull(index.Drives.Single().CheckpointLoss);
    }

    [TestMethod]
    public async Task CheckpointStillInTheJournal_WarmStartsAndReportsNoLoss()
    {
        await SeedCacheAsync();
        using var journal = Journal(CachedJournalId, firstUsn: CachedNextUsn - 500, nextUsn: CachedNextUsn + 4_000);

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockSource.WarmStartedFromCache, drive.BlockSource);
        Assert.IsNull(drive.CheckpointLoss);
    }

    [TestMethod]
    public async Task CheckpointTrimmedAway_RescansAndReportsRoundedSpanPlusMargin()
    {
        await SeedCacheAsync();
        // The journal has moved on past the cached checkpoint by 500 bytes, and its tip is
        // 4000 bytes past it.
        using var journal = Journal(CachedJournalId,
            firstUsn: CachedNextUsn + 500, nextUsn: CachedNextUsn + 4_000, allocationDelta: 64);

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource,
            "an unresumable checkpoint must cold-scan rather than warm-start");
        Assert.AreEqual(DriveState.Ready, drive.State);

        var loss = drive.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.DriveLetter);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(CachedNextUsn + 500, loss.FirstUsn);
        Assert.AreEqual(CachedNextUsn + 4_000, loss.NextUsn);
        Assert.AreEqual(500L, loss.BytesBehind);
        // The 4000-byte span rounds to 4032, then the trimming margin adds one 64-byte delta.
        Assert.AreEqual(4_096L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task JournalRecreated_RescansAndSuggestsNoSize()
    {
        await SeedCacheAsync();
        using var journal = Journal(journalId: 0xFEED, firstUsn: 0, nextUsn: 200);

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);

        var loss = drive.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    public async Task VolumeThatCannotAnswer_WarmStartsAsBefore()
    {
        await SeedCacheAsync();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => null);

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(BlockSource.WarmStartedFromCache, drive.BlockSource,
            "a volume that cannot answer is not evidence against the cached block");
        Assert.IsNull(drive.CheckpointLoss);
    }

    /// <summary>
    ///     An enumeration-backed block carries no journal checkpoint, so nothing about it can
    ///     have fallen out of a journal and the check never runs for it.
    /// </summary>
    [TestMethod]
    public async Task EnumerationBlock_IsNeverCheckedAgainstTheJournal()
    {
        var enumerationOptions = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };
        await using (await FileIndex.OpenAsync(enumerationOptions, CancellationToken.None))
        {
        }

        var checkedDrives = 0;
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ =>
        {
            checkedDrives++;
            return null;
        });

        await using var reopened = await FileIndex.OpenAsync(enumerationOptions, CancellationToken.None);

        Assert.AreEqual(BlockSource.WarmStartedFromCache, reopened.Drives.Single().BlockSource);
        Assert.IsNull(reopened.Drives.Single().CheckpointLoss);
        Assert.AreEqual(0, checkedDrives);
    }

    [TestMethod]
    public async Task UnrepresentableRetentionSize_RescansWithNullHintAndClosesCandidate()
    {
        await SeedCacheAsync();
        using var journal = Journal(CachedJournalId,
            firstUsn: CachedNextUsn + 500, nextUsn: long.MaxValue,
            allocationDelta: 1L << 62);
        var scanCount = 0;
        var options = Options() with
        {
            MftProducer = (request, cancellationToken) =>
            {
                using (var exclusive = new FileStream(request.BlockPath,
                           FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.IsTrue(exclusive.Length > 0);
                }

                scanCount++;
                return ProduceMftShapedBlock(request, cancellationToken);
            }
        };

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, scanCount);
        var drive = reopened.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
        var loss = drive.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(CachedNextUsn + 500, loss.FirstUsn);
        Assert.AreEqual(long.MaxValue, loss.NextUsn);
        Assert.AreEqual(1L << 62, loss.AllocationDelta);
        Assert.AreEqual(500L, loss.BytesBehind);
        Assert.IsNull(loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task UnrepresentableRetentionSize_CacheOnlyDeclinesAndClosesCandidate()
    {
        await SeedCacheAsync();
        using var journal = Journal(CachedJournalId,
            firstUsn: CachedNextUsn + 500, nextUsn: long.MaxValue,
            allocationDelta: 1L << 62);
        var options = Options() with
        {
            InitialOpenCacheOnly = true,
            MftProducer = (_, _) => throw new AssertFailedException("Cache-only must not scan.")
        };

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);

        var drive = reopened.Drives.Single();
        Assert.AreEqual(DriveState.Failed, drive.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, drive.FailureKind);
        Assert.IsNotNull(drive.CheckpointLoss);
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, drive.CheckpointLoss.Cause);
        Assert.IsNull(drive.CheckpointLoss.SizeThatWouldHaveRetained);
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0);
    }

    [TestMethod]
    public async Task CheckpointQueryThrows_PropagatesAndClosesUnpublishedCandidate()
    {
        await SeedCacheAsync();
        var failure = new InvalidOperationException("Injected journal query failure.");
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => throw failure);

        var observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await using var unexpected = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        });

        Assert.AreSame(failure, observed);
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsTrue(exclusive.Length > 0);
    }
}
