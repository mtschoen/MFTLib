using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A volume that comes from an MFT dump is never treated as the live volume that shares its
///     letter: not probed, not journaled, not watched, and opened with options that cannot touch a cache.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftDumpApplicabilityTests
{
    [TestCleanup]
    public void Cleanup() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    [TestMethod]
    public async Task Open_ADumpDriveSharingARealLetterIsNotProbedAsALiveRoot()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        var root = index.Status;
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin);
        Assert.IsFalse(root.Watch.IsSupported, "a dump volume does not support watching");
        Assert.AreEqual(string.Empty, index.CacheDirectoryPath, "a dump index resolves and creates no cache directory");
    }

    [TestMethod]
    public async Task Open_AFailedDumpSettlesAsProducerFailedNotOffline()
    {
        await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(MftDumpIndexes.Fail));

        var root = index.Status;
        Assert.AreEqual(RootState.Failed, root.State);
        Assert.AreEqual(RootFailureKind.ProducerFailed, root.Failure?.Kind);
        Assert.AreEqual("The dump file cannot be opened.", root.Failure?.Message);
        Assert.IsFalse(root.Watch.IsSupported);
    }

    [TestMethod]
    public async Task Open_RejectedCacheOptionsNeverCreateTheCacheDirectory()
    {
        using var cacheDirectoryOwner = new TempDirectory("mftlib-dump-cache", create: false);
        var cacheDirectory = cacheDirectoryOwner.Path;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => FileIndex.OpenAsync(
            MftDumpIndexes.Options() with { CacheDirectory = cacheDirectory }, CancellationToken.None));

        Assert.IsFalse(Directory.Exists(cacheDirectory));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "DumpSourceOnly")]
    [DataRow(true, DisplayName = "DumpExplicitNullRootAndDefaultCache")]
    public async Task Open_WithDefaultRootAndCacheValues_PublishesTheVirtualRoot(bool explicitDefaults)
    {
        var options = MftDumpIndexes.Options();
        if (explicitDefaults)
        {
            options = options with
            {
                Root = null,
                CacheMode = null,
                CacheDirectory = null,
                CacheTag = default
            };
        }

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var root = index.Status;
        Assert.IsTrue(root.Root == new VolumeKey(MftDumpIndexes.DriveLetter));
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.IsNull(root.Block!.CacheLocation, "a dump block is never backed by a cache location");
        Assert.AreEqual(string.Empty, index.CacheDirectoryPath);
        Assert.IsTrue(index.Root(TestContext.CancellationToken).IsDirectory);
    }

    [TestMethod]
    public async Task Open_WithRootSet_IsRejectedNamingRoot()
    {
        var options = MftDumpIndexes.Options() with { Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('D'), MftDumpIndexes.Root, 0) };

        await AssertRejectedAsync(options, "FileIndexOptions.Root");
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

    static async Task AssertRejectedAsync(FileIndexOptions options, string settingName)
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => FileIndex.OpenAsync(options, CancellationToken.None));
        Assert.AreEqual("options", failure.ParamName);
        Assert.Contains(settingName, failure.Message);
    }

    [TestMethod]
    public void Source_ADumpWithAWatchSourceIsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MftIndexSource(MftDumpIndexes.Produce,
            new NullWatchSource(), new MftDumpSourceIdentity(MftDumpIndexes.DumpFilePath, 'D')));
    }

    [TestMethod]
    public async Task QueryUsnJournalSettings_ADumpRootRefusesBeforeTheLiveQuery()
    {
        UsnJournalSettingsQuery.Dependencies._queryOverride =
            volume => throw new AssertFailedException($"The live volume {volume} must not be queried.");
        await using var index = await MftDumpIndexes.OpenAsync();

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(index, i => i.ReadUsnJournalSettings());

        Assert.AreEqual("Volume D: this source has no live journal settings.", failure.Message);
    }

    [TestMethod]
    public async Task QueryUsnJournalSettings_AFailedDumpRootWithNoBlockStillRefuses()
    {
        UsnJournalSettingsQuery.Dependencies._queryOverride =
            volume => throw new AssertFailedException($"The live volume {volume} must not be queried.");
        await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(MftDumpIndexes.Fail));

        var failure = MftDumpIndexes.Throws<InvalidOperationException>(index, i => i.ReadUsnJournalSettings());

        Assert.AreEqual("Volume D: this source has no live journal settings.", failure.Message);
    }

    [TestMethod]
    public async Task StartWatching_ADumpRootIsNotApplicableWithAndWithoutABlock()
    {
        foreach (var producer in new MftBlockProducer[] { MftDumpIndexes.Produce, MftDumpIndexes.Fail })
        {
            await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(producer));

            await RootResultAssertions.NotApplicableAsync(() => index.StartWatchingAsync(CancellationToken.None));
            Assert.IsFalse(index.Status.Watch.IsRequested);
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

        await RootResultAssertions.NotApplicableAsync(() => index.StartWatchingAsync(CancellationToken.None));
        await RootResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(CancellationToken.None));
        Assert.IsFalse(index.Status.Watch.IsRequested);
    }

    [TestMethod]
    public async Task WaitForCatchUp_ADumpRootIsNotApplicable()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        await RootResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task LifecycleVerbs_ADumpRootIsNotApplicableWithNoFailure()
    {
        foreach (var producer in new MftBlockProducer[] { MftDumpIndexes.Produce, MftDumpIndexes.Fail })
        {
            await using var index = await MftDumpIndexes.OpenAsync(MftDumpIndexes.Source(producer));

            var results = new[]
            {
                await index.StartWatchingAsync(CancellationToken.None),
                await index.StopWatchingAsync(CancellationToken.None),
                await index.WaitForCatchUpAsync(CancellationToken.None)
            };

            foreach (var result in results)
            {
                Assert.AreEqual(new VolumeKey('D'), result.Root);
                Assert.AreEqual(RootOperationOutcome.NotApplicable, result.Outcome);
                Assert.IsNull(result.Failure);
            }

            Assert.IsFalse(index.Status.Watch.IsSupported);
            Assert.IsFalse(index.Status.Watch.IsRequested);
        }
    }

    [TestMethod]
    public async Task Rescan_ADumpRootRepublishesADumpBlock()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        await index.RescanAsync(CancellationToken.None);

        Assert.AreEqual("dump:/D/documents", index.Find("dump:/D/documents", TestContext.CancellationToken)!.Value.Path);
        Assert.AreEqual(RootState.Ready, index.Status.State);
        AssertNoWatchStatus(index.Status);
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

            Assert.AreEqual(expected, index.Status.Watch.IsSupported);
            await index.RescanAsync(CancellationToken.None);
            Assert.AreEqual(expected, index.Status.Watch.IsSupported);
        }
    }

    static void AssertNoWatchStatus(RootStatus status)
    {
        Assert.IsFalse(status.Watch.IsSupported);
        Assert.IsFalse(status.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, status.Watch.State);
    }

    sealed class NullWatchSource : IIndexWatchSource
    {
        public Task<IIndexVolumeWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public TestContext TestContext { get; set; } = null!;
}
