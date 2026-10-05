using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointCacheTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="MftIndexSource" /> carries the block producer and the watch source of an index as
///     one object. The journal read is swapped out through
///     <c>JournalCheckpointCheck.OverrideJournalForTest</c>, so these run on every platform.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftIndexSourceTests
{
    const string Reason = "the broker is not running";

    public TestContext TestContext { get; set; } = null!;

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_treeRoot);
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
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

    static IndexedDrive Drive(string root) => new('T', root, 0x0BADF00Du);

    FileIndexOptions Options(MftIndexSource? source) => new()
    {
        Drives = [Drive(_treeRoot)],
        CacheDirectory = _cacheDirectory,
        ProducerPolicy = ProducerPolicy.Mft,
        MftSource = source
    };

    /// <summary>Writes the drive's cache block while the journal still holds its checkpoint.</summary>
    async Task SeedCacheAsync()
    {
        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(Options(new MftIndexSource(ProduceMftShapedBlock)), Token);
        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
    }

    [TestMethod]
    public void Constructor_WithoutProducer_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new MftIndexSource(null!));
    }

    [TestMethod]
    public void Constructor_KeepsProducerAndWatchSourceTogether()
    {
        var watchSource = new FakeIndexWatchSource();

        var source = new MftIndexSource(ProduceMftShapedBlock, watchSource);

        Assert.AreSame(watchSource, source.WatchSource);
        Assert.IsNull(new MftIndexSource(ProduceMftShapedBlock).WatchSource);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Unavailable_WithBlankReason_Throws(string reason)
    {
        Assert.ThrowsException<ArgumentException>(() => MftIndexSource.Unavailable(reason));
    }

    [TestMethod]
    public void Unavailable_WithNullReason_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => MftIndexSource.Unavailable(null!));
    }

    [TestMethod]
    public async Task Unavailable_FailsEachScanWithTheDriveLetterAndTheReason()
    {
        var source = MftIndexSource.Unavailable(Reason);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => source.Producer(new MftBlockProduceRequest { DriveLetter = 'T', VolumeSerial = 1, BlockPath = "unused" },
                Token));

        Assert.AreEqual("Drive T: the broker is not running.", thrown.Message);
        Assert.IsNull(source.WatchSource);
    }

    [TestMethod]
    public async Task Unavailable_ReportsTheDriveAsProducerFailedWithTheMessage()
    {
        await using var index = await FileIndex.OpenAsync(Options(MftIndexSource.Unavailable(Reason)), Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.FailureKind);
        Assert.AreEqual("Drive T: the broker is not running.", status.MftProducerFailureMessage);
    }

    [TestMethod]
    public async Task Unavailable_StillOpensTheCachedBlockButRefusesToWatchWithTheReason()
    {
        await SeedCacheAsync();
        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(Options(MftIndexSource.Unavailable(Reason)), Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        var refusal = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StartWatchingAsync('T', Token));
        Assert.AreEqual("Drive T: the broker is not running.", refusal.Message);
    }

    [TestMethod]
    public async Task SourceWithoutWatchSource_RefusesToWatchAndNamesTheOption()
    {
        await SeedCacheAsync();
        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(Options(new MftIndexSource(ProduceMftShapedBlock)), Token);

        var refusal = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => index.StartWatchingAsync('T', Token));

        StringAssert.Contains(refusal.Message, nameof(FileIndexOptions.MftSource));
    }

    [TestMethod]
    public async Task SourceWithWatchSource_StartsTheWatchOnThatSource()
    {
        await SeedCacheAsync();
        var watchSource = new FakeIndexWatchSource();
        using var journals = OverrideJournals(new Dictionary<char, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(new MftIndexSource(ProduceMftShapedBlock, watchSource)), Token);

        await index.StartWatchingAsync('T', Token);

        Assert.AreEqual(1, watchSource.Starts.Count);
        Assert.AreEqual('T', watchSource.Starts[0].DriveLetter);
    }

    [TestMethod]
    public async Task NoMftSource_IsAConfigurationErrorWhenAMftDriveNeedsAScan()
    {
        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => FileIndex.OpenAsync(Options(null), Token));

        StringAssert.Contains(thrown.Message, nameof(FileIndexOptions.MftSource));
    }

    [TestMethod]
    public async Task NoMftSource_IsIgnoredByTheEnumerationPolicy()
    {
        await using var index = await FileIndex.OpenAsync(
            Options(null) with { ProducerPolicy = ProducerPolicy.Enumeration }, Token);

        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
    }
}
