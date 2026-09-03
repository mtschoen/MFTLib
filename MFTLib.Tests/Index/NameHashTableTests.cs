using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Direct coverage of one sieve pass's bucket-collision behavior and its per-seed bucket
///     assignment, both of which <see cref="DuplicateNameFinder" /> relies on. Two distinct name
///     strings cannot be guaranteed to collide across runs, since
///     <see cref="string.GetHashCode(ReadOnlySpan{char}, StringComparison)" /> for
///     <see cref="StringComparison.OrdinalIgnoreCase" /> is randomized per process, so this
///     exercises the collision path directly on the table with chosen integer hashes instead.
/// </summary>
[TestClass]
public class NameHashTableTests
{
    [TestMethod]
    public void MayRepeat_IsFalseAfterOneIncrementAndTrueAfterTwo()
    {
        var table = new NameHashTable(expectedRowCount: 1000);
        var hash = 12345;

        Assert.IsFalse(table.MayRepeat(hash));

        table.Increment(hash);
        Assert.IsFalse(table.MayRepeat(hash));

        table.Increment(hash);
        Assert.IsTrue(table.MayRepeat(hash));
    }

    [TestMethod]
    public void MayRepeat_ReturnsFalseForAHashNeverIncremented()
    {
        var table = new NameHashTable(expectedRowCount: 8);
        Assert.IsFalse(table.MayRepeat(999));
    }

    [TestMethod]
    public void MayRepeat_TreatsTwoDistinctHashesInTheSameBucketAsBothCandidates()
    {
        // Documented over-count contract: two distinct names whose hashes collide in the same
        // bucket each look like a repeat after only one Increment apiece, because one pass
        // cannot distinguish "this bucket saw the same hash twice" from "this bucket saw two
        // different hashes once each". DuplicateNameFinder relies on later refinement passes,
        // seeded differently, to shrink that false-positive set; a single pass alone never drops
        // a real duplicate.
        var table = new NameHashTable(expectedRowCount: 1);
        var firstHash = 1;
        var firstBucket = table.BucketIndexFor(firstHash);

        var secondHash = -1;
        for (var candidate = 2; candidate < 5_000_000; candidate++)
        {
            if (table.BucketIndexFor(candidate) == firstBucket)
            {
                secondHash = candidate;
                break;
            }
        }

        Assert.AreNotEqual(-1, secondHash, "test setup expects to find a colliding hash within the search bound");

        table.Increment(firstHash);
        table.Increment(secondHash);

        Assert.IsTrue(table.MayRepeat(firstHash));
        Assert.IsTrue(table.MayRepeat(secondHash));
    }

    [TestMethod]
    public void BucketIndexFor_DifferentSeedsScatterASeedZeroCollidingPairIndependently()
    {
        // A refinement chain only shrinks its candidate set if a different seed produces an
        // effectively unrelated bucket assignment for the same hash, not merely an offset one
        // (Fibonacci-multiplying a hash XOR seed alone would keep colliding pairs colliding).
        // Gather a sample of pairs that collide under seed 0 and confirm most of them do not
        // still collide under seed 1.
        var seedZero = NameHashTable.ForBucketCount(NameHashTable.MinimumBucketCount, seed: 0);
        var seedOne = NameHashTable.ForBucketCount(NameHashTable.MinimumBucketCount, seed: 1);

        var firstHashByBucket = new Dictionary<int, int>();
        var collidingPairs = new List<(int First, int Second)>();
        for (var candidate = 0; candidate < 400_000 && collidingPairs.Count < 500; candidate++)
        {
            var bucket = seedZero.BucketIndexFor(candidate);
            if (firstHashByBucket.TryGetValue(bucket, out var earlierHash))
            {
                collidingPairs.Add((earlierHash, candidate));
            }
            else
            {
                firstHashByBucket[bucket] = candidate;
            }
        }

        Assert.IsTrue(collidingPairs.Count >= 100, "test setup expects to find plenty of seed-0 collisions to check");

        var stillCollidingUnderSeedOne = collidingPairs.Count(pair =>
            seedOne.BucketIndexFor(pair.First) == seedOne.BucketIndexFor(pair.Second));

        Assert.IsTrue(stillCollidingUnderSeedOne < collidingPairs.Count / 4,
            $"expected most seed-0 collisions to break under seed 1, but {stillCollidingUnderSeedOne} of " +
            $"{collidingPairs.Count} still collided");
    }

    [TestMethod]
    public void ComputeBucketCount_ForTwentyOneMillionRowsMatchesTheDesignsRealDriveCase()
    {
        // The design note's own example: a 21-million-row volume needs 2^27 buckets, which is
        // also this sieve's hard maximum.
        Assert.AreEqual(NameHashTable.MaximumBucketCount, NameHashTable.ComputeBucketCount(21_000_000));
    }

    [TestMethod]
    public void ComputeBucketCount_ForZeroOrNegativeRowsReturnsTheDefault()
    {
        Assert.AreEqual(1 << 24, NameHashTable.ComputeBucketCount(0));
        Assert.AreEqual(1 << 24, NameHashTable.ComputeBucketCount(-5));
    }

    [TestMethod]
    public void ComputeBucketCount_ForASmallRowCountClampsToTheMinimum()
    {
        Assert.AreEqual(NameHashTable.MinimumBucketCount, NameHashTable.ComputeBucketCount(1));
    }

    [TestMethod]
    public void ComputeBucketCount_ForAHugeRowCountClampsToTheMaximum()
    {
        Assert.AreEqual(NameHashTable.MaximumBucketCount, NameHashTable.ComputeBucketCount(long.MaxValue));
    }

    [TestMethod]
    public void ByteCount_AtTheMaximumBucketCountIsThirtyTwoMebibytes()
    {
        var table = new NameHashTable(expectedRowCount: long.MaxValue);
        Assert.AreEqual(NameHashTable.MaximumBucketCount, table.BucketCount);
        Assert.AreEqual(32L * 1024 * 1024, table.ByteCount);
    }

    [TestMethod]
    public void ByteCount_AtTheDefaultBucketCountIsFourMebibytes()
    {
        var table = new NameHashTable();
        Assert.AreEqual(1 << 24, table.BucketCount);
        Assert.AreEqual(4L * 1024 * 1024, table.ByteCount);
    }

    [TestMethod]
    public void ToMayRepeatBitmap_HalvesTheByteCountSinceOnlySeenAgainSurvives()
    {
        var table = NameHashTable.ForBucketCount(NameHashTable.MinimumBucketCount);
        var bitmap = table.ToMayRepeatBitmap();
        Assert.AreEqual(table.ByteCount / 2, bitmap.ByteCount);
    }

    [TestMethod]
    public void ForBucketCount_RejectsANonPowerOfTwo()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => NameHashTable.ForBucketCount(1000));
    }
}
