using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
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
        MftSource = source
    };

    /// <summary>Writes the drive's cache block while the journal still holds its checkpoint.</summary>
    async Task SeedCacheAsync()
    {
        using var journals = OverrideJournals(new Dictionary<DriveKey, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(Options(new MftIndexSource(ProduceMftShapedBlock)), Token);
        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().Block!.Source);
    }

    [TestMethod]
    public void Constructor_WithoutProducer_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new MftIndexSource(null!));
    }

    [TestMethod]
    public void Constructor_KeepsProducerAndWatchSourceTogether()
    {
        var watchSource = new ScriptedWatchSource();

        var source = new MftIndexSource(ProduceMftShapedBlock, watchSource.IndexSource);

        Assert.AreSame(watchSource.IndexSource, source.WatchSource);
        Assert.IsNull(new MftIndexSource(ProduceMftShapedBlock).WatchSource);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Unavailable_WithBlankReason_Throws(string reason)
    {
        Assert.ThrowsException<ArgumentException>(() => MftIndexSources.Unavailable(reason));
    }

    [TestMethod]
    public void Unavailable_WithNullReason_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => MftIndexSources.Unavailable(null!));
    }

    [TestMethod]
    public async Task Unavailable_FailsEachScanWithTheDriveLetterAndTheReason()
    {
        var source = MftIndexSources.Unavailable(Reason);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => source.Producer(new MftBlockProduceRequest { Drive = 'T', VolumeSerial = 1, BlockPath = "unused" },
                Token));

        Assert.AreEqual("Drive T: the broker is not running.", thrown.Message);
        Assert.IsNull(source.WatchSource);
    }

    [TestMethod]
    public async Task Unavailable_ReportsTheDriveAsProducerFailedWithTheMessage()
    {
        await using var index = await FileIndex.OpenAsync(Options(MftIndexSources.Unavailable(Reason)), Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual("Drive T: the broker is not running.", status.Failure?.Message);
    }

    [TestMethod]
    public async Task Unavailable_StillOpensTheCachedBlockAndReportsWatchingNotApplicable()
    {
        await SeedCacheAsync();
        using var journals = OverrideJournals(new Dictionary<DriveKey, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(Options(MftIndexSources.Unavailable(Reason)), Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], Token));
        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
    }

    [TestMethod]
    public async Task SourceWithoutWatchSource_ReportsWatchingNotApplicable()
    {
        await SeedCacheAsync();
        using var journals = OverrideJournals(new Dictionary<DriveKey, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(Options(new MftIndexSource(ProduceMftShapedBlock)), Token);

        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], Token));

        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
    }

    [TestMethod]
    public async Task SourceWithWatchSource_StartsTheWatchOnThatSource()
    {
        await SeedCacheAsync();
        var watchSource = new ScriptedWatchSource();
        using var journals = OverrideJournals(new Dictionary<DriveKey, JournalWindow> { ['T'] = HealthyWindow });
        await using var index = await FileIndex.OpenAsync(
            Options(new MftIndexSource(ProduceMftShapedBlock, watchSource.IndexSource)), Token);

        await index.StartWatchingAsync(['T'], Token);

        Assert.AreEqual(1, watchSource.Starts.Count);
        Assert.AreEqual('T', watchSource.Starts[0].Drive);
    }

    [TestMethod]
    public async Task NoMftSource_IsAConfigurationErrorWhenAMftDriveNeedsAScan()
    {
        await using var index = await FileIndex.OpenAsync(Options(null), Token);
        var failed = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        StringAssert.Contains(failed.Failure?.Message, nameof(FileIndexOptions.MftSource));
    }

    [TestMethod]
    public async Task NoMftSource_IsIgnoredByTheEnumerationPolicy()
    {
        await using var index = await FileIndex.OpenAsync(
            Options(null) with { Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D, ProducerKind.Enumeration)] }, Token);

        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
    }
}
