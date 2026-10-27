using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Nothing index-wide serializes two volumes: each volume's rescan holds only its own lifecycle
///     gate through production and its own write gate for the commit, and a batch holds only its
///     own root's write gate (spec 5, amendment R8). Overlap is proven with <see cref="TestGate" />
///     holds: both held steps are entered before either is released.
/// </summary>
[TestClass]
public sealed class FileIndexConcurrentRescanTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    protected override bool SeedDocuments => false;

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(CacheDirectoryPath);

    [TestMethod]
    public async Task ConcurrentRescans_BothProducersInsideAtOnce_BothCommit_SnapshotHoldsBoth()
    {
        using var harness = new WatchHarness('T', 'U');
        var heldT = harness.HoldNextProduction('T');
        var heldU = harness.HoldNextProduction('U');

        var rescanT = harness.IndexFor('T').RescanAsync(Token);
        var rescanU = harness.IndexFor('U').RescanAsync(Token);
        await Task.WhenAll(heldT.Entered, heldU.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);
        heldT.Release();
        heldU.Release();
        await Task.WhenAll(rescanT, rescanU).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreSame(harness.BlockFor('T'), harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock.Block);
        Assert.AreSame(harness.BlockFor('U'), harness.IndexFor('U').Root(TestContext.CancellationToken).RootBlock.Block);
    }

    [TestMethod]
    public async Task ConcurrentBlocklessAdoption_BothResolve()
    {
        var producers = new GatedProducers(this);
        await using var indexT = await FileIndex.OpenAsync(producers.CacheOnlyOptions('T'), Token);
        await using var indexU = await FileIndex.OpenAsync(producers.CacheOnlyOptions('U'), Token);

        var rescanT = indexT.RescanAsync(Token);
        var rescanU = indexU.RescanAsync(Token);
        await producers.BothEnteredAsync();
        producers.Release('T');
        producers.Release('U');
        await Task.WhenAll(rescanT, rescanU).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(new VolumeKey('T'), indexT.CurrentBlock!.Root);
        Assert.AreEqual(new VolumeKey('U'), indexU.CurrentBlock!.Root);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConcurrentBlocklessAdoption_OneFailsOneSucceeds_EachKeepsItsOwnStatus(bool failedRootFirst)
    {
        var producers = new GatedProducers(this);
        producers.Fail('T', new IOException("T lost its volume"));
        await using var indexT = await FileIndex.OpenAsync(producers.CacheOnlyOptions('T'), Token);
        await using var indexU = await FileIndex.OpenAsync(producers.CacheOnlyOptions('U'), Token);

        var rescanT = indexT.RescanAsync(Token);
        var rescanU = indexU.RescanAsync(Token);
        await producers.BothEnteredAsync();
        foreach (var driveLetter in failedRootFirst ? "TU" : "UT")
        {
            producers.Release(driveLetter);
            if (driveLetter == 'T')
            {
                await RootResultAssertions.FailureAsync<InvalidOperationException>(() => rescanT.WaitAsync(HangGuard, TestContext.CancellationToken));
            }
            else
            {
                await rescanU.WaitAsync(HangGuard, TestContext.CancellationToken);
            }
        }

        var rootT = indexT.Status;
        var rootU = indexU.Status;
        Assert.AreEqual(RootState.Failed, rootT.State);
        Assert.AreEqual(RootFailureKind.ProducerFailed, rootT.Failure?.Kind);
        Assert.AreEqual("T lost its volume", rootT.Failure?.Message);
        Assert.AreEqual(RootState.Ready, rootU.State);
        Assert.IsNull(rootU.Failure);
        GC.KeepAlive(indexT);
        GC.KeepAlive(indexU);
    }

    [TestMethod]
    public async Task ConcurrentBlocklessAdoption_BothFail_BothMessagesSurvive()
    {
        var producers = new GatedProducers(this);
        producers.Fail('T', new IOException("T lost its volume"));
        producers.Fail('U', new UnauthorizedAccessException("U declined elevation"));
        await using var indexT = await FileIndex.OpenAsync(producers.CacheOnlyOptions('T'), Token);
        await using var indexU = await FileIndex.OpenAsync(producers.CacheOnlyOptions('U'), Token);

        var rescanT = indexT.RescanAsync(Token);
        var rescanU = indexU.RescanAsync(Token);
        await producers.BothEnteredAsync();
        producers.Release('T');
        producers.Release('U');
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => rescanT.WaitAsync(HangGuard, TestContext.CancellationToken));
        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => rescanU.WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual("T lost its volume", indexT.Status.Failure?.Message);
        Assert.AreEqual("U declined elevation", indexU.Status.Failure?.Message);
    }

    [TestMethod]
    public async Task BatchOnT_DoesNotWaitForUCommit()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var commitOfU = harness.TrackGate();
        harness.IndexFor('U').Hooks.PublishInsideWriteGate = driveLetter =>
        {
            if (driveLetter == new VolumeKey('U'))
            {
                commitOfU.MarkEntered();
                commitOfU.WaitForRelease();
            }
        };
        // Off the test's thread: the held commit blocks the thread that reaches it.
        var indexU = harness.IndexFor('U');
        var token = Token;
        var rescanU = Task.Run(() => indexU.RescanAsync(token), token);
        await commitOfU.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);

        await harness.Source.WatchFor('T').Publish(WatchHarness.Batch(9, "during.txt", nextUsn: 700));

        Assert.AreEqual(700L, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        Assert.IsFalse(rescanU.IsCompleted, "T's batch applied while U's commit still held U's write gate");
        commitOfU.Release();
        await rescanU.WaitAsync(HangGuard, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task PublishForU_MidBatchOnT_IsSafe()
    {
        using var harness = new WatchHarness('T', 'U');
        await harness.IndexFor('T').StartWatchingAsync(Token);
        var batchOnT = harness.TrackGate();
        harness.IndexFor('T').Hooks.ApplyJournalEntriesInsideWriteGate = driveLetter =>
        {
            if (driveLetter == new VolumeKey('T'))
            {
                batchOnT.MarkEntered();
                batchOnT.WaitForRelease();
            }
        };
        var consumed = harness.Source.WatchFor('T').Queue(WatchHarness.Batch(9, "held.txt", nextUsn: 700));
        await batchOnT.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        var blockUDuringBatch = harness.IndexFor('U').CurrentBlock;

        await harness.IndexFor('U').RescanAsync(Token).WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreNotSame(blockUDuringBatch, harness.IndexFor('U').CurrentBlock, "U's commit retired the block");
        batchOnT.Release();
        await consumed.WaitAsync(HangGuard, TestContext.CancellationToken);

        var change = harness.Changes.Single(candidate => candidate.Path.EndsWith("held.txt", StringComparison.Ordinal));
        Assert.AreEqual("held.txt", change.Entry.Name);
        Assert.AreEqual(700L, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        Assert.IsNull(harness.RootFor('T').Watch.FailureMessage);
    }

    [TestMethod]
    public async Task ApplyJournalEntries_TakesOnlyItsRootsWriteGate()
    {
        using var harness = new WatchHarness('T', 'U');
        var indexT = harness.IndexFor('T');
        var indexU = harness.IndexFor('U');
        bool? ownGateFree = null;
        bool? otherGatesFree = null;
        indexT.Hooks.ApplyJournalEntriesInsideWriteGate = driveLetter =>
        {
            ownGateFree = indexT.AreRootGatesFreeForTest(driveLetter);
            otherGatesFree = indexU.AreRootGatesFreeForTest(new VolumeKey('U'));
        };

        await indexT.ApplyJournalEntriesAsync('T', [WatchHarness.Create(9, "direct.txt")], WatchHarness.JournalIdentifier, 600);

        Assert.IsFalse(ownGateFree, "the apply holds T's write gate");
        Assert.IsTrue(otherGatesFree, "the apply holds none of U's gates");
        Assert.AreEqual(600L, harness.IndexFor('T').Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
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

        public FileIndexOptions CacheOnlyOptions(char driveLetter) => new()
        {
            Root = SyntheticIndexedRoot.Create(new VolumeKey(driveLetter), owner.TreeRoot, driveLetter == 'T' ? 1u : 2u),
            CacheDirectory = owner.CacheDirectoryPath,
            CacheMode = CacheMode.ExistingOnly,
            MftSource = new MftIndexSource(ProduceAsync)
        };

        async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            var driveLetter = char.ToUpperInvariant((request.Volume).Letter);
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
