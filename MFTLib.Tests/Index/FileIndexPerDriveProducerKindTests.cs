using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed partial class FileIndexPerDriveProducerKindTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string _cacheDirectory => _fixture.CacheDirectory;
    static readonly DateTime Moment = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        File.WriteAllText(Path.Combine(_fixture.TreeRoot, "walked.txt"), "owned tree");
    }

    public void Dispose() => _fixture.Dispose();

    static SyntheticMftProducer Producer() => new(_ =>
    [
        new SyntheticRow(5, ".", 5) { IsDirectory = true },
        new SyntheticRow(6, "scanned.txt", 5)
    ])
    {
        ScanTimestamp = Moment,
        JournalCursor = new SyntheticJournalCursor(7, 4096)
    };

    IndexedDrive Drive(char letter, ProducerKind policy) => new(letter, _fixture.TreeRoot, 123, policy);

    FileIndexOptions Options(params IndexedDrive[] drives) => new()
    {
        Drives = drives,
        CacheDirectory = _fixture.CacheDirectory,
        MftSource = SyntheticIndexSource.Create(Producer())
    };

    [TestMethod]
    public async Task MixedOpen_EachDriveUsesItsProducerAndQueriesCombineTheirRows()
    {
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerKind.Mft), Drive('U', ProducerKind.Enumeration)), CancellationToken.None);

        AssertDrive(index, 'T', ProducerKind.Mft);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
        var rows = index.Search(new SearchQuery(".txt"));
        CollectionAssert.AreEquivalent(new[] { "scanned.txt", "walked.txt" }, rows.Select(row => row.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { 'T', 'U' }, rows.Select(row => row.RecordKey.Drive.Letter).ToArray());

        await index.RescanAsync(['T'], CancellationToken.None);
        await index.RescanAsync(['U'], CancellationToken.None);
        AssertDrive(index, 'T', ProducerKind.Mft);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
    }

    [TestMethod]
    public async Task MixedOpen_MftFailureNeverWalksItsTreeAndEnumerationSiblingIsReady()
    {
        var options = Options(Drive('T', ProducerKind.Mft), Drive('U', ProducerKind.Enumeration)) with
        {
            MftSource = MftIndexSources.Unavailable("scan refused")
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        var failed = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.IsNull(failed.Block);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(new DriveKey('U'), index.Search(new SearchQuery("walked.txt")).Single().RecordKey.Drive);
    }

    [TestMethod]
    public async Task EnumerationOnly_NullMftSourceScansEveryDrive()
    {
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerKind.Enumeration), Drive('U', ProducerKind.Enumeration)) with
        {
            MftSource = null
        }, CancellationToken.None);

        AssertDrive(index, 'T', ProducerKind.Enumeration);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(2, index.Search(new SearchQuery("walked.txt")).Count);
    }

    // A null MftSource is not an admission error: only the drive that needs an MFT scan settles Failed.
    [TestMethod]
    public async Task MixedOpen_NullMftSourceFailsOnlyTheDriveNeedingAScan()
    {
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerKind.Mft), Drive('U', ProducerKind.Enumeration)) with
        {
            MftSource = null
        }, CancellationToken.None);

        var failed = index.Drives.Single(drive => drive.Drive == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.AreEqual("ProducerKind.Mft requires FileIndexOptions.MftSource to be set.", failed.Failure?.Message);
        AssertDrive(index, 'U', ProducerKind.Enumeration);
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
        AssertDrive(index, 'T', ProducerKind.Mft, BlockSource.WarmStartedFromCache);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['T'], CancellationToken.None));
        Assert.IsFalse(index.Drives.Single().Watch.IsRequested);
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
            Drives = [new IndexedDrive('T', _fixture.TreeRoot, 123, Producer: requested)],
            CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent,
            MftSource = requested == ProducerKind.Enumeration ? null : options.MftSource,
            Diagnostics = diagnostics.Add
        }, CancellationToken.None);

        if (cacheOnly)
        {
            var failed = index.Drives.Single();
            Assert.AreEqual(DriveState.Failed, failed.State);
            Assert.AreEqual(DriveFailureKind.CacheDeclined, failed.Failure?.Kind);
            Assert.IsNull(failed.Block);
            Assert.AreEqual(0, index.Search(new SearchQuery(null)).Count);
        }
        else
        {
            AssertDrive(index, 'T', requested);
            var expectedName = requested == ProducerKind.Mft ? "scanned.txt" : "walked.txt";
            var rejectedName = requested == ProducerKind.Mft ? "walked.txt" : "scanned.txt";
            var entry = index.Search(new SearchQuery(".txt")).Single();
            Assert.AreEqual(expectedName, entry.Name);
            Assert.AreEqual(requested, entry.RecordKey.ProducerKind);
            Assert.AreEqual(requested,
                index.Search(new SearchQuery(expectedName)).Single().RecordKey.ProducerKind);
            Assert.AreEqual(0, index.Search(new SearchQuery(rejectedName)).Count);
        }

        Assert.IsTrue(diagnostics.Any(line => line.Contains("producer kind mismatch", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MixedWatch_EnumerationIsUnsupportedWhileMftSiblingStartsAndCatchesUp()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        await using var index = await FileIndex.OpenAsync(Options(
            Drive('T', ProducerKind.Mft), Drive('U', ProducerKind.Enumeration)) with
        {
            MftSource = SyntheticIndexSource.Create(Producer(), watches)
        }, CancellationToken.None);

        Assert.IsFalse(index.Drives.Single(drive => drive.Drive == 'U').Watch.IsSupported);
        await DriveResultAssertions.NotApplicableAsync(
            () => index.StartWatchingAsync(['U'], CancellationToken.None));
        var results = await index.StartWatchingAsync(index.Drives.Select(drive => drive.Drive), CancellationToken.None);
        Assert.AreEqual(DriveOperationOutcome.NotApplicable, results.Single(result => result.Drive == 'U').Outcome);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, results.Single(result => result.Drive == 'T').Outcome);
        await index.WaitForCatchUpAsync(['T'], CancellationToken.None);
        Assert.AreEqual(WatchState.CaughtUp, index.Drives.Single(drive => drive.Drive == 'T').Watch.State);
        Assert.AreEqual(new DriveKey('T'), watches.Starts.Single().Drive);
        await index.RescanAsync(['U'], CancellationToken.None);
        Assert.AreEqual(WatchState.CaughtUp, index.Drives.Single(drive => drive.Drive == 'T').Watch.State);
        Assert.IsFalse(index.Drives.Single(drive => drive.Drive == 'U').Watch.IsSupported);
    }

    static void AssertDrive(FileIndex index, char letter, ProducerKind kind,
        BlockSource source = BlockSource.ProducedByScan)
    {
        var status = index.Drives.Single(drive => drive.Drive == letter);
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.IsNotNull(status.Block);
        Assert.AreEqual(source, status.Block.Source);
        Assert.AreEqual(kind, SyntheticIndexInspection.ReadHeader(index, letter)!.ProducerKind);
    }
}
