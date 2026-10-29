using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NamePool = MFTLibTestExtensions.SyntheticNamePool;
using Support = MFTLib.Tests.Index.PostingQueryTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>When a posting-driven exact query observes cancellation: before any candidate, then at the row-scan cadence.</summary>
[TestClass]
public sealed class PostingQueryCancellationTests
{
    const int Postings = 10_000;
    const string Shared = "shared.txt";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Counts observed checks and cancels its token at the Nth one, so a pass must stop at that checkpoint.</summary>
    sealed class CancelAtCheck(int cancelAtCheck) : IDisposable
    {
        readonly CancellationTokenSource _source = new();

        internal int Checks { get; private set; }

        internal CancellationToken Token => _source.Token;

        internal void Observe()
        {
            if (++Checks == cancelAtCheck)
            {
                _source.Cancel();
            }
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>A block whose one name has <see cref="Postings" /> base rows and a delta chain as long.</summary>
    static SyntheticBlockBuilder BuildBlock()
    {
        var builder = new SyntheticBlockBuilder(slotCapacity: 16384, namePoolCapacity: 1 << 20);
        var root = builder.AddRoot();
        for (var index = 0; index < Postings; index++)
        {
            builder.AddRow(Shared, root, RowFlags.InUse, 1, Support.Moment, 0);
        }

        builder.Complete(Support.Moment);
        var block = builder.OpenForWriting();
        var nameId = SharedId(block);
        for (var index = 0; index < Postings; index++)
        {
            Support.AppendDelta(block, (uint)index + 1, nameId);
        }

        block.Flush(null);
        return builder;
    }

    static uint SharedId(BlockFile block)
    {
        Assert.IsTrue(MFTLib.Index.NamePool.TryFindOrdinal(block, Shared, out var nameId));
        return nameId;
    }

    static int ChecksFor(int steps) => (steps + (int)PostingCandidates.CancellationCheckInterval - 1) /
                                       (int)PostingCandidates.CancellationCheckInterval;

    [TestMethod]
    public void Gather_ChecksOnceFirstThenEveryIntervalOfBaseEntriesAndOfDeltaEntries()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        using var counter = new CancelAtCheck(int.MaxValue);

        using var candidates = PostingCandidates.Gather(block, [SharedId(block)], TestContext.CancellationToken,
            counter.Observe);

        // The pass check, one per 4096-entry slice of the base list, and the delta walk's first entry and every 4096th after.
        Assert.AreEqual(1 + ChecksFor(Postings) + ChecksFor(Postings), counter.Checks);
        Assert.AreEqual(Postings, candidates.Count, "the delta rows repeat the base rows and are de-duplicated");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(6)]
    public void Gather_CancelledAtACheckpoint_StopsThere(int cancelAtCheck)
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        var nameId = SharedId(block);
        using var counter = new CancelAtCheck(cancelAtCheck);

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PostingCandidates.Gather(block, [nameId], counter.Token, counter.Observe));

        Assert.AreEqual(cancelAtCheck, counter.Checks);
    }

    [TestMethod]
    public void Gather_WithACancelledToken_ThrowsBeforeReadingAnyPosting()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        var nameId = SharedId(block);
        var accessesBefore = block.NameRegionAccessCount;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PostingCandidates.Gather(block, [nameId], new CancellationToken(true)));

        Assert.AreEqual(accessesBefore, block.NameRegionAccessCount);
    }

    [TestMethod]
    public void Verification_ChecksOnTheFirstCandidateAndThenEveryInterval()
    {
        using var builder = BuildBlock();
        var block = builder.OpenForWriting();
        using var counter = new CancelAtCheck(int.MaxValue);
        using var candidates = PostingCandidates.Gather(block, [SharedId(block)], TestContext.CancellationToken,
            counter.Observe);
        var checksAfterGather = counter.Checks;

        var verification = candidates.BeginVerification();
        for (var step = 0; step < candidates.Count; step++)
        {
            verification.Step();
        }

        Assert.AreEqual(ChecksFor(Postings), counter.Checks - checksAfterGather);
    }

    [TestMethod]
    public void EveryExactOperation_WithACancelledToken_ThrowsBeforeTheFirstCandidate()
    {
        using var builder = BuildBlock();
        using var opened = new Support.OpenedRoot(builder);
        var root = opened.Root;
        var cancelled = new CancellationToken(true);
        var query = new SearchQuery(Shared, NameMatchMode.Exact);

        Assert.ThrowsExactly<OperationCanceledException>(() => SearchEngine.Search(root, query, cancellationToken: cancelled));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SearchEngine.Enumerate(root, query, cancellationToken: cancelled).ToArray());
        Assert.ThrowsExactly<OperationCanceledException>(() => NamePool.EnumerateRows(root, query, cancelled));
        Assert.ThrowsExactly<OperationCanceledException>(() => LookupEngine.Find(root, "T:/" + Shared, cancelled));
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SearchEngine.SearchExactNames(root, [Shared], new SearchQuery(null), cancellationToken: cancelled));
    }

    [TestMethod]
    public void SearchWithALiveToken_ReturnsEveryPostedRowOnce()
    {
        using var builder = BuildBlock();
        using var opened = new Support.OpenedRoot(builder);

        var results = SearchEngine.Search(opened.Root, new SearchQuery(Shared, NameMatchMode.Exact),
            cancellationToken: TestContext.CancellationToken);

        Assert.AreSequenceEqual(Enumerable.Range(1, Postings).Select(row => (uint)row).ToArray(),
            results.Select(entry => entry.RowIndex).ToArray());
    }
}
