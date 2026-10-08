using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointCacheTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Who owns a <see cref="JournalCheckpointLoss" /> and how long it lives: a drive that
///     never settles must not leave its report behind for whichever drive reuses its ordinal,
///     a cache-only open that adopts a block despite a lost checkpoint must still say why, and
///     a rescan that replaces the block must drop a report that no longer explains the block
///     in place.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCheckpointLossLifetimeTests
{
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
            MftSource = new MftIndexSource(producer),
            InitialOpenCacheOnly = cacheOnly
        };
    }

    static JournalWindow Recreated => new(0xFEED, 0, 200, 64, 128L * 1024 * 1024);

    async Task SeedCacheAsync(params IndexedDrive[] drives)
    {
        using var journals = OverrideJournals(drives.ToDictionary(drive => drive.DriveLetter, _ => HealthyWindow));
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, drives: drives), CancellationToken.None);
        foreach (var drive in index.Drives)
        {
            Assert.AreEqual(BlockSource.ProducedByScan, drive.Block.Source);
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

        using var journals = OverrideJournals(new Dictionary<char, JournalWindow>
        {
            ['T'] = TrimmedWindow,   // T loses its checkpoint
            ['U'] = HealthyWindow    // U can still resume
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
        Assert.IsNotNull(failed.Watch.CheckpointLoss, "the failed drive keeps the report that describes it");
        Assert.AreEqual('T', failed.Watch.CheckpointLoss.DriveLetter);

        var settled = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.AreEqual(BlockSource.WarmStartedFromCache, settled.Block.Source);
        Assert.IsNull(settled.Watch.CheckpointLoss,
            "a drive that reused the failed drive's ordinal must not inherit its report");
    }

    // --- Cache-only ---

    /// <summary>
    ///     The surface that matters most for a cache-only consumer: a cache-only open never
    ///     watches and the block is still a correct snapshot as of its age, so it is adopted
    ///     despite the lost checkpoint, and the status still says why the checkpoint could not
    ///     be resumed and the size a journal would need to be at least to have kept it.
    /// </summary>
    [TestMethod]
    public async Task CacheOnlyAdoptsATrimmedCheckpoint_ReportsWhyAndTheSize()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = TrimmedWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, cacheOnly: true, drives: drive), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(BlockSource.WarmStartedFromCache, status.Block.Source);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);

        var loss = status.Watch.CheckpointLoss;
        Assert.IsNotNull(loss, "a cache-only open must still say why the checkpoint could not be resumed");
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual('T', loss.DriveLetter);
        Assert.AreEqual(CachedNextUsn, loss.CheckpointUsn);
        Assert.AreEqual(500L, loss.BytesBehind);
        Assert.AreEqual(4_096L, loss.SizeThatWouldHaveRetained);
    }

    [TestMethod]
    public async Task CacheOnlyAdoptsOverARecreatedJournal_ReportsTheLossWithNoSize()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = Recreated });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, cacheOnly: true, drives: drive), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);

        var loss = status.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "different journal instances have no comparable span");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    public async Task CacheOnlyOverAHealthyBlock_WarmStartsAndReportsNoLoss()
    {
        var drive = Drive('T', _firstTreeRoot);
        await SeedCacheAsync(drive);

        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, cacheOnly: true, drives: drive), CancellationToken.None);

        var status = index.Drives.Single();
        Assert.AreEqual(BlockSource.WarmStartedFromCache, status.Block.Source);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);
        Assert.IsNull(status.Watch.CheckpointLoss);
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

        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = TrimmedWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(ProduceMftShapedBlock, drives: drive), CancellationToken.None);
        Assert.IsNotNull(index.Drives.Single().Watch.CheckpointLoss);

        await index.RescanAsync('T', CancellationToken.None);

        Assert.IsNull(index.Drives.Single().Watch.CheckpointLoss,
            "the rescan replaced the block the report described");
    }
}
