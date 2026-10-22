using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Nothing index-wide serializes two drives: each drive's rescan holds only its own lifecycle
///     gate through production and its own write gate for the commit, and a batch holds only its
///     own drive's write gate (spec 5, amendment R8). Overlap is proven with <see cref="TestGate" />
///     holds: both held steps are entered before either is released.
/// </summary>
[TestClass]
public sealed class FileIndexConcurrentRescanTests : IDisposable
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;
    string CacheDirectoryPath => _fixture.CacheDirectory;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        Directory.CreateDirectory(CacheDirectoryPath);
    }

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    public async Task ConcurrentRescans_BothProducersInsideAtOnce_BothCommit_SnapshotHoldsBoth()
    {
        using var harness = new WatchHarness('T', 'U');
        var heldT = harness.HoldNextProduction('T');
        var heldU = harness.HoldNextProduction('U');

        var rescanT = harness.Index.RescanAsync(['T'], Token);
        var rescanU = harness.Index.RescanAsync(['U'], Token);
        await Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard);
        heldT.Release();
        heldU.Release();
        await Task.WhenAll(rescanT, rescanU).WaitAsync(HangGuard);

        Assert.AreSame(harness.BlockFor('T'), harness.Index.Root('T').DriveBlock.Block);
        Assert.AreSame(harness.BlockFor('U'), harness.Index.Root('U').DriveBlock.Block);
    }

    [TestMethod]
    public async Task ConcurrentBlocklessAdoption_DistinctOrdinals_BothResolve()
    {
        var producers = new GatedProducers(this);
        await using var index = await FileIndex.OpenAsync(producers.CacheOnlyOptions(), Token);

        var rescanT = index.RescanAsync(['T'], Token);
        var rescanU = index.RescanAsync(['U'], Token);
        await producers.BothEnteredAsync();
        producers.Release('T');
        producers.Release('U');
        await Task.WhenAll(rescanT, rescanU).WaitAsync(HangGuard);

        Assert.IsTrue(index.TryGetDriveOrdinal('T', out var ordinalT));
        Assert.IsTrue(index.TryGetDriveOrdinal('U', out var ordinalU));
        Assert.AreNotEqual(ordinalT, ordinalU);
        Assert.AreEqual('T', index.CurrentSnapshot[ordinalT].Drive);
        Assert.AreEqual('U', index.CurrentSnapshot[ordinalU].Drive);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConcurrentBlocklessAdoption_OneFailsOneSucceeds_EachKeepsItsOwnStatus(bool failedDriveFirst)
    {
        var producers = new GatedProducers(this);
        producers.Fail('T', new IOException("T lost its volume"));
        await using var index = await FileIndex.OpenAsync(producers.CacheOnlyOptions(), Token);

        var rescanT = index.RescanAsync(['T'], Token);
        var rescanU = index.RescanAsync(['U'], Token);
        await producers.BothEnteredAsync();
        foreach (var driveLetter in failedDriveFirst ? "TU" : "UT")
        {
            producers.Release(driveLetter);
            if (driveLetter == 'T')
            {
                await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => rescanT.WaitAsync(HangGuard));
            }
            else
            {
                await rescanU.WaitAsync(HangGuard);
            }
        }

        var driveT = index.Drives.Single(drive => drive.Drive == 'T');
        var driveU = index.Drives.Single(drive => drive.Drive == 'U');
        Assert.AreEqual(DriveState.Failed, driveT.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, driveT.Failure?.Kind);
        Assert.AreEqual("T lost its volume", driveT.Failure?.Message);
        Assert.AreEqual(DriveState.Ready, driveU.State);
        Assert.IsNull(driveU.Failure);
        GC.KeepAlive(index);
    }

    [TestMethod]
    public async Task ConcurrentBlocklessAdoption_BothFail_BothMessagesSurvive()
    {
        var producers = new GatedProducers(this);
        producers.Fail('T', new IOException("T lost its volume"));
        producers.Fail('U', new UnauthorizedAccessException("U declined elevation"));
        await using var index = await FileIndex.OpenAsync(producers.CacheOnlyOptions(), Token);

        var rescanT = index.RescanAsync(['T'], Token);
        var rescanU = index.RescanAsync(['U'], Token);
        await producers.BothEnteredAsync();
        producers.Release('T');
        producers.Release('U');
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => rescanT.WaitAsync(HangGuard));
        await DriveResultAssertions.FailureAsync<InvalidOperationException>(() => rescanU.WaitAsync(HangGuard));

        Assert.AreEqual("T lost its volume",
            index.Drives.Single(drive => drive.Drive == 'T').Failure?.Message);
        Assert.AreEqual("U declined elevation",
            index.Drives.Single(drive => drive.Drive == 'U').Failure?.Message);
    }

    [TestMethod]
    public async Task BatchOnT_DoesNotWaitForUCommit()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var commitOfU = harness.TrackGate();
        harness.Index.Hooks.PublishInsideWriteGate = driveLetter =>
        {
            if (driveLetter == 'U')
            {
                commitOfU.MarkEntered();
                commitOfU.WaitForRelease();
            }
        };
        // Off the test's thread: the held commit blocks the thread that reaches it.
        var index = harness.Index;
        var token = Token;
        var rescanU = Task.Run(() => index.RescanAsync(['U'], token));
        await commitOfU.Entered.WaitAsync(HangGuard);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "during.txt", nextUsn: 700));

        Assert.AreEqual(700L, harness.Index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.IsFalse(rescanU.IsCompleted, "T's batch applied while U's commit still held U's write gate");
        commitOfU.Release();
        await rescanU.WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task PublishForU_MidBatchOnT_IsSafe()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.Index.StartWatchingAsync(['T'], Token);
        var batchOnT = harness.TrackGate();
        harness.Index.Hooks.ApplyJournalEntriesInsideWriteGate = driveLetter =>
        {
            if (driveLetter == 'T')
            {
                batchOnT.MarkEntered();
                batchOnT.WaitForRelease();
            }
        };
        var consumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await batchOnT.Entered.WaitAsync(HangGuard);
        var snapshotDuringBatch = harness.Index.CurrentSnapshot;

        await harness.Index.RescanAsync(['U'], Token).WaitAsync(HangGuard);
        Assert.AreNotSame(snapshotDuringBatch, harness.Index.CurrentSnapshot, "U's commit retired the snapshot");
        batchOnT.Release();
        await consumed.WaitAsync(HangGuard);

        var change = harness.Changes.Single(candidate => candidate.Path.EndsWith("held.txt", StringComparison.Ordinal));
        Assert.AreEqual("held.txt", change.Entry.Name);
        Assert.AreEqual(700L, harness.Index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.IsNull(harness.DriveFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public void ApplyJournalEntries_TakesOnlyItsDrivesWriteGate()
    {
        using var harness = new WatchHarness('T', 'U');
        var index = harness.Index;
        bool? ownGateFree = null;
        bool? otherGatesFree = null;
        index.Hooks.ApplyJournalEntriesInsideWriteGate = driveLetter =>
        {
            ownGateFree = index.AreDriveGatesFreeForTest((driveLetter).Letter);
            otherGatesFree = index.AreDriveGatesFreeForTest('U');
        };

        index.ApplyJournalEntries('T', [WatchHarness.Create(9, "direct.txt")], WatchHarness.JournalIdentifier, 600);

        Assert.IsFalse(ownGateFree, "the apply holds T's write gate");
        Assert.IsTrue(otherGatesFree, "the apply holds none of U's gates");
        Assert.AreEqual(600L, harness.Index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
    }

    /// <summary>
    ///     Two cache-only-declined drives over one tree, each with a producer parked on its own
    ///     gate that writes a real block at the requested path, or throws the failure set for it.
    /// </summary>
    sealed class GatedProducers(FileIndexConcurrentRescanTests owner)
    {
        readonly ConcurrentDictionary<char, TestGate> _gates = new()
        {
            ['T'] = new TestGate(),
            ['U'] = new TestGate()
        };

        readonly ConcurrentDictionary<char, Exception> _failures = [];

        public void Fail(char driveLetter, Exception failure) => _failures[driveLetter] = failure;

        public void Release(char driveLetter) => _gates[driveLetter].Release();

        /// <summary>
        ///     Completes once both producers are parked. On a timeout both gates are released
        ///     first, so the failing test reports instead of wedging its own disposal.
        /// </summary>
        public async Task BothEnteredAsync()
        {
            try
            {
                await Task.WhenAll(_gates['T'].Entered, _gates['U'].Entered).WaitAsync(HangGuard);
            }
            catch (TimeoutException)
            {
                Release('T');
                Release('U');
                throw;
            }
        }

        public FileIndexOptions CacheOnlyOptions() => new()
        {
            Drives = [new IndexedDrive('T', owner.TreeRoot, 1), new IndexedDrive('U', owner.TreeRoot, 2)],
            CacheDirectory = owner.CacheDirectoryPath,
            CacheMode = CacheMode.ExistingOnly,
            MftSource = new MftIndexSource(ProduceAsync)
        };

        async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            var driveLetter = char.ToUpperInvariant((request.Drive).Letter);
            var gate = _gates[driveLetter];
            gate.MarkEntered();
            await gate.WaitForReleaseAsync(cancellationToken);
            if (_failures.TryGetValue(driveLetter, out var failure))
            {
                throw failure;
            }

            SeededBlocks.Write(request.BlockPath, request.VolumeSerial,
                journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
            return new MftBlockProduceResult(BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                SkippedRecordCount: 0);
        }
    }
}
