using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed partial class FileIndexPerDriveProducerKindTests : IndexTreeTestBase
{
    static readonly DateTime Moment = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    protected override bool SeedDocuments => false;

    [TestInitialize]
    public void Initialize() => File.WriteAllText(Path.Combine(TreeRoot, "walked.txt"), "owned tree");

    static SyntheticMftProducer Producer() => new(_ =>
    [
        new SyntheticRow(5, ".", 5) { IsDirectory = true },
        new SyntheticRow(6, "scanned.txt", 5)
    ])
    {
        ScanTimestamp = Moment,
        JournalCursor = new SyntheticJournalCursor(7, 4096)
    };

    IndexedRoot Drive(char letter, ProducerKind policy) =>
        SyntheticIndexedDrive.Create(new VolumeKey(letter), Fixture.TreeRoot, 123, policy);

    FileIndexOptions Options(IndexedRoot root) => new()
    {
        Root = root,
        CacheDirectory = Fixture.CacheDirectory,
        MftSource = SyntheticIndexSource.Create(Producer())
    };

    [TestMethod]
    public async Task MixedOpen_EachDriveUsesItsProducerAndSetQueriesCombineTheirRows()
    {
        await using var indexT = await FileIndex.OpenAsync(Options(Drive('T', ProducerKind.Mft)), CancellationToken.None);
        await using var indexU = await FileIndex.OpenAsync(Options(Drive('U', ProducerKind.Enumeration)), CancellationToken.None);
        var set = new FileIndexSet([indexT, indexU]);

        AssertDrive(indexT, 'T', ProducerKind.Mft);
        AssertDrive(indexU, 'U', ProducerKind.Enumeration);
        var rows = set.Search(new SearchQuery(".txt"), TestContext.CancellationToken);
        Assert.AreSequenceEqual(["scanned.txt", "walked.txt"], [.. rows.Select(row => row.Name)], SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([new VolumeKey('T'), new VolumeKey('U')], [.. rows.Select(row => row.RecordKey.Drive)], SequenceOrder.InAnyOrder);

        await indexT.RescanAsync(CancellationToken.None);
        await indexU.RescanAsync(CancellationToken.None);
        AssertDrive(indexT, 'T', ProducerKind.Mft);
        AssertDrive(indexU, 'U', ProducerKind.Enumeration);
    }

    [TestMethod]
    public async Task MixedOpen_MftFailureNeverWalksItsTreeAndEnumerationSiblingIsReady()
    {
        await using var indexT = await FileIndex.OpenAsync(Options(Drive('T', ProducerKind.Mft)) with { MftSource = null }, CancellationToken.None);
        await using var indexU = await FileIndex.OpenAsync(Options(Drive('U', ProducerKind.Enumeration)) with { MftSource = null }, CancellationToken.None);
        var set = new FileIndexSet([indexT, indexU]);

        var failed = indexT.Status;
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.MftSourceMissing, failed.Failure?.Kind);
        Assert.IsNull(failed.Block);
        AssertDrive(indexU, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(new VolumeKey('U'), set.Search(new SearchQuery("walked.txt"), TestContext.CancellationToken).Single().RecordKey.Drive);

        var resultT = await indexT.RescanAsync(CancellationToken.None);
        var resultU = await indexU.RescanAsync(CancellationToken.None);
        Assert.AreEqual(DriveOperationOutcome.Failed, resultT.Outcome);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, resultU.Outcome);
        Assert.AreEqual(DriveFailureKind.MftSourceMissing, indexT.Status.Failure?.Kind);
        AssertDrive(indexU, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(new VolumeKey('U'), set.Search(new SearchQuery("walked.txt"), TestContext.CancellationToken).Single().Drive);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnumerationOnly_IgnoresMftSource(bool configured)
    {
        var invocationCount = 0;
        var mftSource = configured ? new MftIndexSource((_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new InvalidOperationException("the MFT producer must not be called");
        }) : null;
        await using var indexT = await FileIndex.OpenAsync(Options(Drive('T', ProducerKind.Enumeration)) with { MftSource = mftSource }, CancellationToken.None);
        await using var indexU = await FileIndex.OpenAsync(Options(Drive('U', ProducerKind.Enumeration)) with { MftSource = mftSource }, CancellationToken.None);
        var set = new FileIndexSet([indexT, indexU]);

        AssertDrive(indexT, 'T', ProducerKind.Enumeration);
        AssertDrive(indexU, 'U', ProducerKind.Enumeration);
        Assert.IsNull(indexT.Status.Failure);
        Assert.IsNull(indexU.Status.Failure);

        Assert.AreSequenceEqual([new VolumeKey('T'), new VolumeKey('U')], [.. set.Search(new SearchQuery("walked.txt"), TestContext.CancellationToken).Select(entry => entry.Drive)], SequenceOrder.InAnyOrder);
        Assert.AreEqual(0, invocationCount);
    }

    // A null MftSource is not an admission error: only the drive that needs an MFT scan settles Failed.
    [TestMethod]
    public async Task MixedOpen_NullMftSourceFailsOnlyTheDriveNeedingAScan()
    {
        await using var indexT = await FileIndex.OpenAsync(Options(SyntheticIndexedDrive.Create(new VolumeKey('T'), Fixture.TreeRoot, 123)) with { MftSource = null }, CancellationToken.None);
        await using var indexU = await FileIndex.OpenAsync(Options(Drive('U', ProducerKind.Enumeration)) with { MftSource = null }, CancellationToken.None);
        var set = new FileIndexSet([indexT, indexU]);

        var failed = indexT.Status;
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.MftSourceMissing, failed.Failure?.Kind);
        Assert.AreEqual("Drive T: no MFT source is available to scan this drive.", failed.Failure?.Message);
        Assert.IsNull(failed.Block);
        AssertDrive(indexU, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(new VolumeKey('U'), set.Search(new SearchQuery("walked.txt"), TestContext.CancellationToken).Single().Drive);
    }

    [TestMethod]
    public async Task WarmMftDrive_NullSourceNeedsNeitherScanNorWatchUntilStartIsRequested()
    {
        var options = Options(Drive('T', ProducerKind.Mft));
        await using (var seeded = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            AssertDrive(seeded, 'T', ProducerKind.Mft);
        }

        await using var index = await FileIndex.OpenAsync(options with { MftSource = null }, CancellationToken.None);
        AssertDrive(index, 'T', ProducerKind.Mft, BlockOrigin.WarmStartedFromCache);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(CancellationToken.None));
        Assert.IsFalse(index.Status.Watch.IsRequested);
    }

    [TestMethod]
    [DataRow(ProducerKind.Mft, false)]
    [DataRow(ProducerKind.Enumeration, false)]
    [DataRow(ProducerKind.Mft, true)]
    [DataRow(ProducerKind.Enumeration, true)]
    public async Task CacheProducerMismatch_ColdScansOrDeclinesCacheOnly(ProducerKind requested, bool cacheOnly)
    {
        var stored = requested == ProducerKind.Mft ? ProducerKind.Enumeration : ProducerKind.Mft;
        var options = Options(Drive('T', stored));
        await using (var seeded = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            AssertDrive(seeded, 'T', stored);
        }

        var diagnostics = new List<string>();
        await using var index = await FileIndex.OpenAsync(options with
        {
            Root = SyntheticIndexedDrive.Create(new VolumeKey('T'), Fixture.TreeRoot, 123, producer: requested),
            CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent,
            MftSource = requested == ProducerKind.Enumeration ? null : options.MftSource,
            Diagnostics = diagnostics.Add
        }, CancellationToken.None);

        if (cacheOnly)
        {
            var failed = index.Status;
            Assert.AreEqual(DriveState.Failed, failed.State);
            Assert.AreEqual(DriveFailureKind.CacheDeclined, failed.Failure?.Kind);
            Assert.IsNull(failed.Block);
            Assert.IsEmpty(index.Search(new SearchQuery(null), TestContext.CancellationToken));
        }
        else
        {
            AssertDrive(index, 'T', requested);
            var expectedName = requested == ProducerKind.Mft ? "scanned.txt" : "walked.txt";
            var rejectedName = requested == ProducerKind.Mft ? "walked.txt" : "scanned.txt";
            var entry = index.Search(new SearchQuery(".txt"), TestContext.CancellationToken).Single();
            Assert.AreEqual(expectedName, entry.Name);
            Assert.AreEqual(requested, entry.RecordKey.ProducerKind);
            Assert.AreEqual(requested,
                index.Search(new SearchQuery(expectedName), TestContext.CancellationToken).Single().RecordKey.ProducerKind);
            Assert.IsEmpty(index.Search(new SearchQuery(rejectedName), TestContext.CancellationToken));
        }

        Assert.Contains(line => line.Contains("producer kind mismatch", StringComparison.Ordinal), diagnostics);
    }

    [TestMethod]
    public async Task MixedWatch_EnumerationIsUnsupportedWhileMftSiblingStartsAndCatchesUp()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        var source = SyntheticIndexSource.Create(Producer(), watches);
        await using var indexT = await FileIndex.OpenAsync(Options(Drive('T', ProducerKind.Mft)) with { MftSource = source }, CancellationToken.None);
        await using var indexU = await FileIndex.OpenAsync(Options(Drive('U', ProducerKind.Enumeration)) with { MftSource = source }, CancellationToken.None);

        Assert.IsFalse(indexU.Status.Watch.IsSupported);
        await DriveResultAssertions.NotApplicableAsync(
            () => indexU.StartWatchingAsync(CancellationToken.None));
        var resultT = await indexT.StartWatchingAsync(CancellationToken.None);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, resultT.Outcome);
        await indexT.WaitForCatchUpAsync(CancellationToken.None);
        Assert.AreEqual(WatchState.CaughtUp, indexT.Status.Watch.State);
        Assert.AreEqual(new VolumeKey('T'), watches.Starts.Single().Drive);
        await indexU.RescanAsync(CancellationToken.None);
        Assert.AreEqual(WatchState.CaughtUp, indexT.Status.Watch.State);
        Assert.IsFalse(indexU.Status.Watch.IsSupported);
    }

    static void AssertDrive(FileIndex index, char letter, ProducerKind kind,
        BlockOrigin origin = BlockOrigin.ProducedByScan)
    {
        var status = index.Status;
        Assert.AreEqual(new VolumeKey(letter), status.Drive);
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.IsNotNull(status.Block);
        Assert.AreEqual(origin, status.Block.Origin);
        Assert.AreEqual(kind, SyntheticIndexInspection.ReadHeader(index, new VolumeKey(letter))!.ProducerKind);
    }

    public TestContext TestContext { get; set; } = null!;
}
