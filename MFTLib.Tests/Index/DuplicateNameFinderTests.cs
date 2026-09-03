using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     End-to-end coverage of the refinement chain: a small forced bucket count (via
///     <see cref="DuplicateNameSieveOptions" />) reproduces the high collision rate a real
///     multi-million-row drive would see under its own bucket count, on a synthetic block small
///     enough to build in a test. This is the deterministic stand-in for testing the design
///     note's own 21-million-unique-name example directly, which would require building and
///     scanning a 21-million-row block.
/// </summary>
[TestClass]
public class DuplicateNameFinderTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    // Small enough to build quickly, large enough that a 2^16-bucket sieve's pass 0 sees a
    // substantial collision rate (birthday paradox: about a quarter of rows collide with another
    // row's bucket on the first pass) for the refinement chain to actually have work to do.
    const int UniqueNameCount = 20_000;
    static readonly DuplicateNameSieveOptions ForcedSmallSieve = new(bucketCountOverride: NameHashTable.MinimumBucketCount);

    [TestMethod]
    public void Find_WithTwentyThousandUniqueNamesMaterializesAtMostOnePercentOfRows()
    {
        using var builder = new SyntheticBlockBuilder('U',
            slotCapacity: UniqueNameCount + 10, namePoolCapacity: (uint)UniqueNameCount * 40);
        var root = builder.AddRoot();
        for (var index = 0; index < UniqueNameCount; index++)
        {
            builder.AddRow($"unique-{index}.bin", root, RowFlags.InUse, index, Moment);
        }

        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('U', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var groups = DuplicateNameFinder.Find(snapshot, ForcedSmallSieve, out var statistics);

            Assert.AreEqual(0, groups.Count);
            Assert.IsTrue(statistics.NamesMaterialized <= UniqueNameCount / 100,
                $"expected at most 1 percent of {UniqueNameCount} rows to reach materialization, " +
                $"got {statistics.NamesMaterialized}");

            // Each pass only admits rows that already passed every earlier pass, so the admitted
            // set can never grow pass over pass; that is guaranteed by construction, not just
            // statistically likely, so assert it as a hard invariant. The whole point of refining
            // is that it also shrinks substantially overall rather than staying proportional to
            // the row count throughout, which is the statistical claim.
            Assert.IsTrue(statistics.CandidatesPerPass.Count >= 2,
                "expected pass 0's high collision rate to trigger at least one refinement pass");
            for (var passIndex = 1; passIndex < statistics.CandidatesPerPass.Count; passIndex++)
            {
                Assert.IsTrue(statistics.CandidatesPerPass[passIndex] <= statistics.CandidatesPerPass[passIndex - 1],
                    "expected each refinement pass to admit no more rows than the pass before it");
            }

            Assert.IsTrue(statistics.CandidatesPerPass[^1] < statistics.CandidatesPerPass[0] / 2,
                "expected the refinement chain to substantially shrink the candidate set overall");
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_TrueDuplicatesSurviveEveryRefinementPassUnderTheForcedSmallSieve()
    {
        using var builder = new SyntheticBlockBuilder('D',
            slotCapacity: UniqueNameCount + 20, namePoolCapacity: (uint)UniqueNameCount * 40);
        var root = builder.AddRoot();
        for (var index = 0; index < UniqueNameCount; index++)
        {
            builder.AddRow($"unique-{index}.bin", root, RowFlags.InUse, index, Moment);
        }

        builder.AddRow("dup-a.bin", root, RowFlags.InUse, 1, Moment);
        builder.AddRow("dup-a.bin", root, RowFlags.InUse, 2, Moment);
        builder.AddRow("dup-b.bin", root, RowFlags.InUse, 3, Moment);
        builder.AddRow("dup-b.bin", root, RowFlags.InUse, 4, Moment);
        builder.AddRow("dup-b.bin", root, RowFlags.InUse, 5, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('D', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var groups = DuplicateNameFinder.Find(snapshot, ForcedSmallSieve, out _);

            Assert.AreEqual(2, groups.Count);
            var byName = groups.ToDictionary(group => group.Name);
            Assert.AreEqual(2, byName["dup-a.bin"].Entries.Count);
            Assert.AreEqual(3, byName["dup-b.bin"].Entries.Count);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void ShouldStopRefining_TrueWhenCandidateCountIsSmallRelativeToBucketCount()
    {
        Assert.IsTrue(DuplicateNameFinder.ShouldStopRefining(
            candidateCount: 100, previousCandidateCount: 10_000, bucketCount: 1 << 16, passIndex: 1));
    }

    [TestMethod]
    public void ShouldStopRefining_TrueWhenCandidateCountDidNotHalve()
    {
        // 6000 is not below half of 10000 (5000) and is well above the small-relative-to-bucket-
        // count floor, so this isolates the "stopped improving" condition.
        Assert.IsTrue(DuplicateNameFinder.ShouldStopRefining(
            candidateCount: 6000, previousCandidateCount: 10_000, bucketCount: 1 << 20, passIndex: 1));
    }

    [TestMethod]
    public void ShouldStopRefining_TrueAtTheHardMaximumRegardlessOfCandidateCount()
    {
        // 40000 is comfortably above the small-relative-to-bucket-count floor (4096) and well
        // below half of the previous count (50000), so only the hard maximum forces the stop.
        Assert.IsTrue(DuplicateNameFinder.ShouldStopRefining(
            candidateCount: 40_000, previousCandidateCount: 100_000, bucketCount: 1 << 20,
            passIndex: DuplicateNameFinder.MaximumRefinementPassCount));
    }

    [TestMethod]
    public void ShouldStopRefining_FalseWhenCandidatesAreStillShrinkingFastAndFarFromTheFloor()
    {
        Assert.IsFalse(DuplicateNameFinder.ShouldStopRefining(
            candidateCount: 10_000, previousCandidateCount: 100_000, bucketCount: 1 << 20, passIndex: 1));
    }

    [TestMethod]
    public void PeakChainMemory_AtTheMaximumBucketCountAndHardMaximumPassCountIsEightyMebibytes()
    {
        // Every pass but the one currently being built has already handed off just its
        // seenAgain bitmap (see NameHashTable.ToMayRepeatBitmap); the pass under construction
        // still holds both arrays. Measured through real ByteCount properties, not GC.
        var totalPassCount = DuplicateNameFinder.MaximumRefinementPassCount + 1;
        long peakBytes = 0;
        for (var passIndex = 0; passIndex < totalPassCount - 1; passIndex++)
        {
            var completedTable = NameHashTable.ForBucketCount(NameHashTable.MaximumBucketCount, passIndex);
            peakBytes += completedTable.ToMayRepeatBitmap().ByteCount;
        }

        var buildingTable = NameHashTable.ForBucketCount(NameHashTable.MaximumBucketCount, totalPassCount - 1);
        peakBytes += buildingTable.ByteCount;

        Assert.AreEqual(80L * 1024 * 1024, peakBytes);
    }
}
