using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Who owns a <see cref="JournalCheckpointLoss" /> and how long it lives: a drive that
///     never settles must not leave its report behind for whichever drive reuses its ordinal,
///     a cache-only open must still say why it declined the cache, and a rescan that replaces
///     the block must drop a report that no longer explains the block in place.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCheckpointLossLifetimeTests
{
    const ulong CachedJournalId = 0xABCD;
    const long CachedNextUsn = 1_000_000;
    static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    readonly List<string> _directories = [];
    string _firstTreeRoot = null!;
    string _secondTreeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _firstTreeRoot = NewDirectory();
        _secondTreeRoot = NewDirectory();
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        _directories.Add(_cacheDirectory);
    }

    string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "Documents"));
        File.WriteAllText(Path.Combine(path, "Documents", "readme.md"), "hello");
        _directories.Add(path);
        return path;
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in _directories)
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

        _directories.Clear();
    }

    static IndexedDrive Drive(char letter, string root)
    {
        return new IndexedDrive(letter, root, letter == 'T' ? 0x0BADF00Du : 0x0BADBEEFu);
    }

    FileIndexOptions Options(MftBlockProducer producer, bool cacheOnly = false, params IndexedDrive[] drives)
    {
        return new FileIndexOptions
        {
            Drives = drives.Length > 0 ? drives : [Drive('T', _firstTreeRoot)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = producer,
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

    static JournalWindow Recreated => new(0xFEED, 0, 200, 64, 128L * 1024 * 1024);

    async Task SeedCacheAsync(params IndexedDrive[] drives)
    {
        using var journals = Journals(drives.ToDictionary(drive => drive.DriveLetter, _ => Healthy));
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, drives: drives), CancellationToken.None);
        foreach (var drive in index.Drives)
        {
            Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
        }
    }

    // --- Ordinal reuse ---

    /// <summary>
    ///     A drive that loses its checkpoint and then fails to scan never adds a block, so the
    ///     next drive takes the same ordinal. The report must go with the drive it describes,
    ///     not with the ordinal.
    /// </summary>
    [TestMethod]
    public async Task FailedDriveKeepsItsOwnLoss_AndTheNextDriveOnThatOrdinalGetsNone()
    {
        var first = Drive('T', _firstTreeRoot);
        var second = Drive('U', _secondTreeRoot);
        await SeedCacheAsync(first, second);

        using var journals = Journals(new Dictionary<char, JournalWindow>
        {
            ['T'] = Trimmed,   // T loses its checkpoint
            ['U'] = Healthy    // U can still resume
        });

        // T's cold scan then fails, so T never occupies an ordinal and U reuses it.
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            return request.DriveLetter == 'T'
                ? throw new IOException("synthetic scan failure for T")
                : ProduceMftShapedBlock(request, token);
        }

        await using var index = await FileIndex.OpenAsync(
            Options(Producer, drives: [first, second]), CancellationToken.None);

        var failed = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.IsNotNull(failed.CheckpointLoss, "the failed drive keeps the report that describes it");
        Assert.AreEqual('T', failed.CheckpointLoss.DriveLetter);

        var settled = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.AreEqual(BlockSource.WarmStartedFromCache, settled.BlockSource);
        Assert.IsNull(settled.CheckpointLoss,
            "a drive that reused the failed drive's ordinal must not inherit its report");
    }

    // --- Cache-only ---

    /// <summary>
    ///     The surface that matters most for a cache-only consumer: the cache was declined, and
    ///     this is why, and this is the journal size that would have prevented it.
    /// </summary>
    [TestMethod]
    public async Task CacheOnlyDeclinedOverATrimmedCheckpoint_ReportsWhyAndTheSize()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, cacheOnly: true, drives: drive), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, status.FailureKind);

        var loss = status.CheckpointLoss;
        Assert.IsNotNull(loss, "a cache-only open must still say why the cache was unusable");
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.DriveLetter);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(500L, loss.BytesBehind);
        Assert.AreEqual(4_032L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task CacheOnlyDeclinedOverARecreatedJournal_ReportsTheLossWithNoSize()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Recreated });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, cacheOnly: true, drives: drive), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveFailureKind.CacheDeclined, status.FailureKind);

        var loss = status.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "no journal size would have kept it");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    public async Task CacheOnlyOverAHealthyBlock_WarmStartsAndReportsNoLoss()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Healthy });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, cacheOnly: true, drives: drive), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(BlockSource.WarmStartedFromCache, status.BlockSource);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);
        Assert.IsNull(status.CheckpointLoss);
    }

    // --- Rescan ---

    /// <summary>
    ///     The report explains the block that is in place. A rescan replaces that block, so the
    ///     report stops applying and must not be rendered again after one.
    /// </summary>
    [TestMethod]
    public async Task ASuccessfulRescanClearsTheLoss()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = Journals(new Dictionary<char, JournalWindow> { ['T'] = Trimmed });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, drives: drive), CancellationToken.None);
        Assert.IsNotNull(index.Drives.Single().CheckpointLoss);

        await index.RescanAsync('T', CancellationToken.None);

        Assert.IsNull(index.Drives.Single().CheckpointLoss,
            "the rescan replaced the block the report described");
    }
}
