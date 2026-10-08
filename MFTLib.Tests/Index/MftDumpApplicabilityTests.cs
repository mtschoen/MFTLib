using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A drive that comes from an MFT dump is never treated as the live volume that shares its
///     letter: not probed, not journaled, not watched, and opened with options that cannot touch a cache.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftDumpApplicabilityTests
{
    const string RootMessage = "A dump root must be dump:/D.";
    const string KeyMessage = "The dump source requires exactly its configured logical drive key.";
    const string CacheMessage = "Dump sources require NoCache and do not accept cache options.";

    [TestMethod]
    public async Task Open_ADumpDriveSharingARealLetterIsNotProbedAsALiveRoot()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockSource.ProducedByScan, drive.Block.Source);
        Assert.IsFalse(drive.Watch.Supported, "a dump drive does not support watching");
        Assert.AreEqual(string.Empty, index.CacheDirectoryPath, "a dump index resolves and creates no cache directory");
    }

    [TestMethod]
    public async Task Open_AFailedDumpSettlesAsProducerFailedNotOffline()
    {
        await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(MftDumpIndexes.Fail));

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, drive.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, drive.FailureKind);
        Assert.AreEqual("The dump file cannot be opened.", drive.FailureMessage);
        Assert.IsFalse(drive.Watch.Supported);
    }

    [TestMethod]
    public async Task Open_RejectedCacheOptionsNeverCreateTheCacheDirectory()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-dump-cache-{Guid.NewGuid():N}");

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => FileIndex.OpenAsync(
            MftDumpIndexes.Options() with { CacheDirectory = cacheDirectory }, CancellationToken.None));

        Assert.IsFalse(Directory.Exists(cacheDirectory));
    }

    [TestMethod]
    public async Task Open_ValidationTable_RejectsEachOptionWithItsMessage()
    {
        var table = new (string Description, FileIndexOptions Options, string Message)[]
        {
            ("another key", WithDrives(new IndexedDrive('E', "dump:/E", 0)), KeyMessage),
            ("two drives",
                WithDrives(new IndexedDrive('D', MftDumpIndexes.Root, 0), new IndexedDrive('E', "dump:/E", 0)),
                KeyMessage),
            ("no drives", WithDrives(), KeyMessage),
            ("a host root", WithDrives(new IndexedDrive('D', Path.GetTempPath(), 0)), RootMessage),
            ("a lower-case root", WithDrives(new IndexedDrive('D', "dump:/d", 0)), RootMessage),
            ("a trailing separator", WithDrives(new IndexedDrive('D', "dump:/D/", 0)), RootMessage),
            ("a volume serial", WithDrives(new IndexedDrive('D', MftDumpIndexes.Root, 1)),
                "A dump source requires VolumeSerial to be zero."),
            ("caching", MftDumpIndexes.Options() with { NoCache = false }, CacheMessage),
            ("a cache directory", MftDumpIndexes.Options() with { CacheDirectory = "elsewhere" }, CacheMessage),
            ("cache-only", MftDumpIndexes.Options() with { InitialOpenCacheOnly = true }, CacheMessage),
            ("a cache tag", MftDumpIndexes.Options() with { CacheTag = new CacheTag("ABCD", 1) }, CacheMessage),
            ("enumeration", MftDumpIndexes.Options() with { ProducerPolicy = ProducerPolicy.Enumeration },
                "Dump sources require ProducerPolicy.Mft.")
        };

        foreach (var (description, options, message) in table)
        {
            var failure = await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => FileIndex.OpenAsync(options, CancellationToken.None), description);
            Assert.AreEqual("options", failure.ParamName, description);
            StringAssert.StartsWith(failure.Message, message, description);
        }
    }

    [TestMethod]
    public async Task Open_ALowerCaseDriveKeyOfTheConfiguredLetterIsAccepted()
    {
        await using var index = await FileIndex.OpenAsync(
            MftDumpIndexes.Options() with { Drives = [new IndexedDrive('d', MftDumpIndexes.Root, 0)] },
            CancellationToken.None);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
    }

    [TestMethod]
    public void Source_ADumpWithAWatchSourceIsRefused()
    {
        Assert.ThrowsException<ArgumentException>(() => new MftIndexSource(MftDumpIndexes.Produce,
            new NullWatchSource(), new MftDumpSourceIdentity(MftDumpIndexes.DumpFilePath, 'D')));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_ADumpDriveRefusesBeforeTheLiveQuery()
    {
        using var restore = UsnJournalSettingsQuery.OverrideQueryForTest(
            drive => throw new AssertFailedException($"The live volume {drive} must not be queried."));
        await using var index = await MftDumpIndexes.OpenAsync();

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(index, i => i.QueryUsnJournalSettings('d'));

        Assert.AreEqual("Drive D: this source has no live journal settings.", failure.Message);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_AFailedDumpDriveWithNoBlockStillRefuses()
    {
        using var restore = UsnJournalSettingsQuery.OverrideQueryForTest(
            drive => throw new AssertFailedException($"The live volume {drive} must not be queried."));
        await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(MftDumpIndexes.Fail));

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(index, i => i.QueryUsnJournalSettings('D'));

        Assert.AreEqual("Drive D: this source has no live journal settings.", failure.Message);
    }

    [TestMethod]
    public async Task StartWatching_ADumpDriveIsRefusedWithAndWithoutABlock()
    {
        foreach (var producer in new MftBlockProducer[] { MftDumpIndexes.Produce, MftDumpIndexes.Fail })
        {
            await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(producer));

            var failure = await MftDumpIndexes.ThrowsAsync<InvalidOperationException>(
                index, i => i.StartWatchingAsync('D', CancellationToken.None));

            Assert.AreEqual("Drive D: this source does not support watching.", failure.Message);
            Assert.IsFalse(index.Drives.Single().Watch.Requested);
        }
    }

    [TestMethod]
    public async Task StartWatching_ASourceWithNoWatchSourceIsRefusedTheSameWay()
    {
        var directories = new OwnedIndexDirectories();
        Directory.CreateDirectory(directories.TreeRoot);
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', directories.TreeRoot, 0)],
            NoCache = true,
            CacheDirectory = directories.CacheDirectory,
            MftSource = new MftIndexSource(MftDumpIndexes.Produce)
        };
        try
        {
            await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

            var failure = await MftDumpIndexes.ThrowsAsync<InvalidOperationException>(
                index, i => i.StartWatchingAsync('T', CancellationToken.None));
            var catchUp = MftDumpIndexes.Throws<InvalidOperationException>(
                index, i => i.WaitForCatchUpAsync('T', CancellationToken.None));

            Assert.AreEqual("Drive T: this source does not support watching.", failure.Message);
            Assert.AreEqual("Drive T: this source does not support watching.", catchUp.Message);
        }
        finally
        {
            directories.Dispose();
        }
    }

    [TestMethod]
    public async Task WaitForCatchUp_ADumpDriveIsRefused()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(
            index, i => i.WaitForCatchUpAsync('D', CancellationToken.None));

        Assert.AreEqual("Drive D: this source does not support watching.", failure.Message);
    }

    [TestMethod]
    public async Task Batched_ADumpDriveIsNotApplicableWithNoFailure()
    {
        foreach (var producer in new MftBlockProducer[] { MftDumpIndexes.Produce, MftDumpIndexes.Fail })
        {
            await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(producer));
            char[] letters = ['D'];

            var results = new[]
            {
                await index.StartWatchingAsync(letters, CancellationToken.None),
                await index.StopWatchingAsync(letters, CancellationToken.None),
                await index.WaitForCatchUpAsync(letters, CancellationToken.None),
                await index.StartWatchingAsync(CancellationToken.None)
            };

            foreach (var result in results)
            {
                var only = result.Single();
                Assert.AreEqual('D', only.DriveLetter);
                Assert.AreEqual(DriveOperationOutcome.NotApplicable, only.Outcome);
                Assert.IsNull(only.Failure);
            }

            Assert.IsFalse(index.Drives.Single().Watch.Supported);
            Assert.IsFalse(index.Drives.Single().Watch.Requested);
        }
    }

    [TestMethod]
    public async Task Rescan_ADumpDriveRepublishesADumpBlock()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        await index.RescanAsync('D', CancellationToken.None);

        Assert.AreEqual("dump:/D/documents", index.Find("dump:/D/documents")!.Value.Path);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        AssertNoWatchStatus(index.Drives.Single());
    }

    [TestMethod]
    public async Task WatchSupported_ASourceWithNoWatchSourceIsFalseAndOneWithAWatchSourceIsTrue()
    {
        foreach (var (watchSource, expected) in new (IIndexWatchSource? Source, bool Expected)[]
                 {
                     (null, false), (new NullWatchSource(), true)
                 })
        {
            var directories = new OwnedIndexDirectories();
            Directory.CreateDirectory(directories.TreeRoot);
            try
            {
                await using var index = await FileIndex.OpenAsync(new FileIndexOptions
                {
                    Drives = [new IndexedDrive('T', directories.TreeRoot, 0)],
                    NoCache = true,
                    CacheDirectory = directories.CacheDirectory,
                    MftSource = new MftIndexSource(MftDumpIndexes.Produce, watchSource)
                }, CancellationToken.None);

                Assert.AreEqual(expected, index.Drives.Single().Watch.Supported);
                await index.RescanAsync('T', CancellationToken.None);
                Assert.AreEqual(expected, index.Drives.Single().Watch.Supported);
            }
            finally
            {
                directories.Dispose();
            }
        }
    }

    static void AssertNoWatchStatus(DriveStatus status)
    {
        Assert.IsFalse(status.Watch.Supported);
        Assert.IsFalse(status.Watch.Requested);
        Assert.AreEqual(WatchCatchUpState.NotStarted, status.Watch.CatchUpState);
    }

    static FileIndexOptions WithDrives(params IndexedDrive[] drives) =>
        MftDumpIndexes.Options() with { Drives = drives };

    sealed class NullWatchSource : IIndexWatchSource
    {
        public Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
