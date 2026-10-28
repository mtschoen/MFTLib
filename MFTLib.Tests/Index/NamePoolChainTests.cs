using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>Chain traversal under a live writer, its cancellation cadence, and the writer's long-chain index.</summary>
[TestClass]
public sealed class NamePoolChainTests
{
    const string CasingBase = "abcdefghijklmnop";
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Cancels its token at the Nth observed check, so a traversal must stop at that checkpoint.</summary>
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

    /// <summary>The spelling of <see cref="CasingBase" /> with letter i upper-cased when bit i of the mask is set.</summary>
    static string Casing(int mask)
    {
        var characters = CasingBase.ToCharArray();
        for (var bit = 0; bit < 13; bit++)
        {
            if ((mask & (1 << bit)) != 0)
            {
                characters[bit] = char.ToUpperInvariant(characters[bit]);
            }
        }

        return new string(characters);
    }

    static SyntheticBlockBuilder BuildCasingBlock(int casings, out uint[] ids)
    {
        var builder = new SyntheticBlockBuilder(slotCapacity: 16384, namePoolCapacity: 1 << 20);
        var block = builder.OpenForWriting();
        ids = new uint[casings];
        for (var mask = 0; mask < casings; mask++)
        {
            Assert.IsTrue(NamePool.TryGetOrAdd(block, Casing(mask), out ids[mask]));
        }

        return builder;
    }

    [TestMethod]
    public void ChainWalk_AnAppendBetweenTheHeadAndCountReads_DoesNotHideAPublishedName()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "alpha", out var alphaId));
        var appended = false;
        void AppendSameChainName()
        {
            if (!appended)
            {
                appended = true;
                Assert.IsTrue(NamePool.TryGetOrAdd(block, "ALPHA", out _));
            }
        }

        var found = NamePool.TryFindOrdinal(block, "alpha", AppendSameChainName, TestContext.CancellationToken, out var foundId);

        Assert.IsTrue(appended);
        Assert.IsTrue(found, "The chain published before the lookup began must stay visible.");
        Assert.AreEqual(alphaId, foundId);
    }

    [TestMethod]
    public void CaseSensitiveExact_ObservesCancellationEvery4096VisitedEntries()
    {
        using var builder = BuildCasingBlock(4100, out _);
        var block = builder.OpenForWriting();
        var checks = 0;

        var found = NamePool.TryFindOrdinal(block, Casing(8191), () => checks++, TestContext.CancellationToken, out _);

        Assert.IsFalse(found);
        // One at walk setup, one after 4096 visited entries.
        Assert.AreEqual(2, checks);

        using var canceller = new CancelAtCheck(2);
        var cancelled = false;
        try
        {
            NamePool.TryFindOrdinal(block, Casing(8191), canceller.Observe, canceller.Token, out _);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.IsTrue(cancelled);
        Assert.AreEqual(2, canceller.Checks);
    }

    [TestMethod]
    public void IgnoreCaseExact_CountsTagRejectedEntriesTowardTheCheckpointCadence()
    {
        using var builder = new SyntheticBlockBuilder(slotCapacity: 8192, namePoolCapacity: 1 << 20);
        var block = builder.OpenForWriting();
        var mask = block.BucketCount - 1;
        const int chainLength = 4100;
        var target = NameHash.Compute("target") & mask;
        var added = 0;
        string? pattern = null;
        for (var index = 0; added <= chainLength; index++)
        {
            var name = $"n-{index}";
            if ((NameHash.Compute(name) & mask) != target || name == "target")
            {
                continue;
            }

            if (added == chainLength)
            {
                pattern = name;
            }
            else
            {
                Assert.IsTrue(NamePool.TryGetOrAdd(block, name, out _));
            }

            added++;
        }

        // The pattern sits in the same bucket as the whole chain but is not in it, and most chain entries carry
        // another tag, so the walker rejects them without returning.
        var checks = 0;
        using (new NameQueryMatches(block, new SearchQuery(pattern, NameMatchMode.Exact), () => checks++,
                   TestContext.CancellationToken))
        {
        }

        // Before setup, after the count is fixed, walk setup, and once after 4096 visited entries.
        Assert.AreEqual(4, checks);

        using var canceller = new CancelAtCheck(4);
        var cancelled = false;
        try
        {
            using var names = new NameQueryMatches(block, new SearchQuery(pattern, NameMatchMode.Exact),
                canceller.Observe, canceller.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.IsTrue(cancelled);
        Assert.AreEqual(4, canceller.Checks);
    }

    [TestMethod]
    public void Interning_ManyCasingsOfOneFoldedName_DoesNotRescanTheChainPerName()
    {
        const int casings = 2000;
        using var builder = new SyntheticBlockBuilder(slotCapacity: 16384, namePoolCapacity: 1 << 20);
        var block = builder.OpenForWriting();
        var ids = new uint[casings];

        var before = block.NameRegionAccessCount;
        for (var mask = 0; mask < casings; mask++)
        {
            Assert.IsTrue(NamePool.TryGetOrAdd(block, Casing(mask), out ids[mask]));
        }

        var adding = block.NameRegionAccessCount - before;
        before = block.NameRegionAccessCount;
        for (var mask = 0; mask < casings; mask++)
        {
            Assert.IsTrue(NamePool.TryGetOrAdd(block, Casing(mask), out var again));
            Assert.AreEqual(ids[mask], again);
        }

        var hitting = block.NameRegionAccessCount - before;

        Assert.AreEqual(casings, ids.Distinct().Count());
        // A chain scan per name would be about casings * casings / 2 pool reads.
        Assert.IsLessThan(40L * casings, adding);
        Assert.IsLessThan(40L * casings, hitting);
        Assert.IsNull(MFTLibTestExtensions.SyntheticNamePool.CheckIntegrity(block));
    }

    /// <summary>Names that all land in one bucket; with 16 tag bits they almost always carry different tags.</summary>
    static List<string> NamesInOneBucket(BlockFile block, int count)
    {
        var mask = block.BucketCount - 1;
        var target = NameHash.Compute("target-bucket") & mask;
        var names = new List<string>(count);
        for (var index = 0; names.Count < count; index++)
        {
            var name = $"b-{index}";
            if ((NameHash.Compute(name) & mask) == target)
            {
                names.Add(name);
            }
        }

        return names;
    }

    [TestMethod]
    public void Interning_ManyDifferentTagNamesInOneBucket_StaysAmortizedLinear()
    {
        const int count = 2000;
        using var builder = new SyntheticBlockBuilder(slotCapacity: 8192, namePoolCapacity: 1 << 20);
        var block = builder.OpenForWriting();
        var names = NamesInOneBucket(block, count);
        var ids = new uint[count];

        var before = block.ChainLinkVisitCount;
        for (var index = 0; index < count; index++)
        {
            Assert.IsTrue(NamePool.TryGetOrAdd(block, names[index], out ids[index]));
        }

        var adding = block.ChainLinkVisitCount - before;
        before = block.ChainLinkVisitCount;
        for (var index = 0; index < count; index++)
        {
            Assert.IsTrue(NamePool.TryGetOrAdd(block, names[index], out var again));
            Assert.AreEqual(ids[index], again);
        }

        var hitting = block.ChainLinkVisitCount - before;

        Assert.IsLessThan(40L * count, adding);
        Assert.IsLessThan(40L * count, hitting);
        Assert.IsNull(MFTLibTestExtensions.SyntheticNamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void Interning_SeveralCollidingCasingGroupsInOneBucket_BuildTheBucketIndexOnce()
    {
        const int groups = 20;
        const int casingsPerGroup = 100;
        using var builder = new SyntheticBlockBuilder(slotCapacity: 8192, namePoolCapacity: 1 << 20);
        var block = builder.OpenForWriting();
        var mask = block.BucketCount - 1;
        var target = NameHash.Compute("target-bucket") & mask;
        var bases = new List<string>();
        for (var index = 0; bases.Count < groups; index++)
        {
            var name = $"abcdefghijk-{index}";
            if ((NameHash.Compute(name) & mask) == target)
            {
                bases.Add(name);
            }
        }

        static string Variant(string baseName, int casing)
        {
            var characters = baseName.ToCharArray();
            for (var bit = 0; bit < 11; bit++)
            {
                if ((casing & (1 << bit)) != 0)
                {
                    characters[bit] = char.ToUpperInvariant(characters[bit]);
                }
            }

            return new string(characters);
        }

        var before = block.ChainLinkVisitCount;
        var ids = new Dictionary<string, uint>();
        for (var casing = 0; casing < casingsPerGroup; casing++)
        {
            foreach (var baseName in bases)
            {
                var name = Variant(baseName, casing);
                Assert.IsTrue(NamePool.TryGetOrAdd(block, name, out var id));
                ids[name] = id;
            }
        }

        var adding = block.ChainLinkVisitCount - before;
        before = block.ChainLinkVisitCount;
        foreach (var (name, id) in ids)
        {
            Assert.IsTrue(NamePool.TryGetOrAdd(block, name, out var again));
            Assert.AreEqual(id, again);
        }

        var hitting = block.ChainLinkVisitCount - before;
        var total = groups * casingsPerGroup;

        Assert.AreEqual(total, ids.Values.Distinct().Count());
        Assert.IsLessThan(40L * total, adding);
        Assert.IsLessThan(40L * total, hitting);
        Assert.IsNull(MFTLibTestExtensions.SyntheticNamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void Interning_LongChainIndexIsProcessLocalAndAbsentFromAReopenedBlock()
    {
        using var builder = BuildCasingBlock(100, out var ids);
        builder.AddRoot();
        builder.Complete(Moment);
        using var reopened = builder.OpenForReading(out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(0L, reopened.NameRegionAccessCount);
        Assert.IsTrue(NamePool.TryGetOrAdd(reopened, Casing(57), out var found));
        Assert.AreEqual(ids[57], found);
        Assert.IsTrue(NamePool.TryGetOrAdd(reopened, Casing(8000), out var added));
        Assert.AreEqual(101u, added);
        Assert.IsTrue(NamePool.TryGetOrAdd(reopened, Casing(8000), out var addedAgain));
        Assert.AreEqual(added, addedAgain);
    }
}
