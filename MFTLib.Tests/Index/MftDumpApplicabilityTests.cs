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
    [TestCleanup]
    [SupportedOSPlatform("windows")]
    public void Cleanup() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    [TestMethod]
    public async Task Open_ADumpDriveSharingARealLetterIsNotProbedAsALiveRoot()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, drive.Block!.Origin);
        Assert.IsFalse(drive.Watch.IsSupported, "a dump drive does not support watching");
        Assert.AreEqual(string.Empty, index.CacheDirectoryPath, "a dump index resolves and creates no cache directory");
    }

    [TestMethod]
    public async Task Open_AFailedDumpSettlesAsProducerFailedNotOffline()
    {
        await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(MftDumpIndexes.Fail));

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, drive.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, drive.Failure?.Kind);
        Assert.AreEqual("The dump file cannot be opened.", drive.Failure?.Message);
        Assert.IsFalse(drive.Watch.IsSupported);
    }

    [TestMethod]
    public async Task Open_RejectedCacheOptionsNeverCreateTheCacheDirectory()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-dump-cache-{Guid.NewGuid():N}");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => FileIndex.OpenAsync(
            MftDumpIndexes.Options() with { CacheDirectory = cacheDirectory }, CancellationToken.None));

        Assert.IsFalse(Directory.Exists(cacheDirectory));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "DumpSourceOnly")]
    [DataRow(true, DisplayName = "DumpExplicitEmptyDrivesAndDefaultCache")]
    public async Task Open_WithDefaultDriveAndCacheValues_PublishesTheVirtualDrive(bool explicitDefaults)
    {
        var options = MftDumpIndexes.Options();
        if (explicitDefaults)
        {
            options = options with
            {
                Drives = [],
                CacheMode = null,
                CacheDirectory = null,
                CacheTag = default
            };
        }

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var drive = index.Drives.Single();
        Assert.IsTrue(drive.Drive == MftDumpIndexes.DriveLetter);
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.IsNull(drive.Block!.CacheLocation, "a dump block is never backed by a cache location");
        Assert.AreEqual(string.Empty, index.CacheDirectoryPath);
        Assert.IsTrue(index.Root(MftDumpIndexes.DriveLetter).IsDirectory);
    }

    [TestMethod]
    public async Task Open_WithDrivesSet_IsRejectedNamingDrives()
    {
        var options = MftDumpIndexes.Options() with { Drives = [new IndexedDrive('D', MftDumpIndexes.Root, 0)] };

        await AssertRejectedAsync(options, "FileIndexOptions.Drives");
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.ExistingOnly)]
    [DataRow(CacheMode.Disabled)]
    public async Task Open_WithAnyCacheModeSet_IsRejectedNamingCacheMode(CacheMode mode)
    {
        await AssertRejectedAsync(MftDumpIndexes.Options() with { CacheMode = mode }, "FileIndexOptions.CacheMode");
    }

    [TestMethod]
    public async Task Open_WithCacheDirectorySet_IsRejectedNamingCacheDirectory()
    {
        await AssertRejectedAsync(MftDumpIndexes.Options() with { CacheDirectory = "elsewhere" },
            "FileIndexOptions.CacheDirectory");
    }

    [TestMethod]
    public async Task Open_WithCacheTagSet_IsRejectedNamingCacheTag()
    {
        await AssertRejectedAsync(MftDumpIndexes.Options() with { CacheTag = new CacheTag("ABCD", 1) },
            "FileIndexOptions.CacheTag");
    }

    [TestMethod]
    public async Task Open_WithNullDrives_IsRejectedNamingDrivesWithoutProducing()
    {
        var producerCalls = 0;
        var source = MftDumpIndexes.Source((request, token) =>
        {
            Interlocked.Increment(ref producerCalls);
            return MftDumpIndexes.Produce(request, token);
        });

        await AssertRejectedAsync(MftDumpIndexes.Options(source) with { Drives = null! }, "FileIndexOptions.Drives");

        Assert.AreEqual(0, producerCalls);
    }

    static async Task AssertRejectedAsync(FileIndexOptions options, string settingName)
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => FileIndex.OpenAsync(options, CancellationToken.None));
        Assert.AreEqual("options", failure.ParamName);
        StringAssert.Contains(failure.Message, settingName);
    }

    [TestMethod]
    public void Source_ADumpWithAWatchSourceIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MftIndexSource(MftDumpIndexes.Produce,
            new NullWatchSource(), new MftDumpSourceIdentity(MftDumpIndexes.DumpFilePath, 'D')));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_ADumpDriveRefusesBeforeTheLiveQuery()
    {
        UsnJournalSettingsQuery.Dependencies.QueryOverride =
            drive => throw new AssertFailedException($"The live volume {drive} must not be queried.");
        await using var index = await MftDumpIndexes.OpenAsync();

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(index, i => i.QueryUsnJournalSettings('d'));

        Assert.AreEqual("Drive D: this source has no live journal settings.", failure.Message);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_AFailedDumpDriveWithNoBlockStillRefuses()
    {
        UsnJournalSettingsQuery.Dependencies.QueryOverride =
            drive => throw new AssertFailedException($"The live volume {drive} must not be queried.");
        await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(MftDumpIndexes.Fail));

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(index, i => i.QueryUsnJournalSettings('D'));

        Assert.AreEqual("Drive D: this source has no live journal settings.", failure.Message);
    }

    [TestMethod]
    public async Task StartWatching_ADumpDriveIsNotApplicableWithAndWithoutABlock()
    {
        foreach (var producer in new MftBlockProducer[] { MftDumpIndexes.Produce, MftDumpIndexes.Fail })
        {
            await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(producer));

            await DriveResultAssertions.NotApplicableAsync(() => index.StartWatchingAsync(['D'], CancellationToken.None));
            Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
        }
    }

    [TestMethod]
    public async Task StartWatching_ASourceWithNoWatchSourceIsNotApplicable()
    {
        using var fixture = new IndexTreeFixture(seedDocuments: false);
        var options = fixture.Options(volumeSerial: 0, producer: ProducerKind.Mft, cacheMode: CacheMode.Disabled) with
        {
            MftSource = new MftIndexSource(MftDumpIndexes.Produce)
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        await DriveResultAssertions.NotApplicableAsync(() => index.StartWatchingAsync(['T'], CancellationToken.None));
        await DriveResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(['T'], CancellationToken.None));
        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
    }

    [TestMethod]
    public async Task WaitForCatchUp_ADumpDriveIsNotApplicable()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        await DriveResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(['D'], CancellationToken.None));
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
                await index.StartWatchingAsync(letters.Select(letter => new DriveKey(letter)), CancellationToken.None),
                await index.StopWatchingAsync(letters.Select(letter => new DriveKey(letter)), CancellationToken.None),
                await index.WaitForCatchUpAsync(letters.Select(letter => new DriveKey(letter)), CancellationToken.None),
                await index.StartWatchingAsync(index.Drives.Select(drive => drive.Drive),CancellationToken.None)
            };

            foreach (var result in results)
            {
                var only = result.Single();
                Assert.AreEqual('D', only.Drive);
                Assert.AreEqual(DriveOperationOutcome.NotApplicable, only.Outcome);
                Assert.IsNull(only.Failure);
            }

            Assert.IsFalse(index.Drives.Single().Watch.IsSupported);
            Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
        }
    }

    [TestMethod]
    public async Task Rescan_ADumpDriveRepublishesADumpBlock()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        await index.RescanAsync(['D'], CancellationToken.None);

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
            using var fixture = new IndexTreeFixture(seedDocuments: false);
            await using var index = await FileIndex.OpenAsync(
                fixture.Options(volumeSerial: 0, producer: ProducerKind.Mft, cacheMode: CacheMode.Disabled) with
                {
                    MftSource = new MftIndexSource(MftDumpIndexes.Produce, watchSource)
                }, CancellationToken.None);

            Assert.AreEqual(expected, index.Drives.Single().Watch.IsSupported);
            await index.RescanAsync(['T'], CancellationToken.None);
            Assert.AreEqual(expected, index.Drives.Single().Watch.IsSupported);
        }
    }

    static void AssertNoWatchStatus(DriveStatus status)
    {
        Assert.IsFalse(status.Watch.IsSupported);
        Assert.IsFalse(status.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, status.Watch.State);
    }

    sealed class NullWatchSource : IIndexWatchSource
    {
        public Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
