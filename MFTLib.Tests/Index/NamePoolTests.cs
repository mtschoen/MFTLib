using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NamePool = MFTLibTestExtensions.SyntheticNamePool;

namespace MFTLib.Tests.Index;

[TestClass]
public class NamePoolTests
{
    [TestMethod]
    public void TryGetOrAdd_HashTagAndBucketCollisionStillComparesTheNameBytes()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        var seen = new Dictionary<uint, string>();
        string? first = null;
        string? second = null;
        for (var index = 0; index < 200_000; index++)
        {
            var name = $"collision-{index}";
            var hash = NamePool.Hash(name);
            var key = (uint)(hash & (block.BucketCount - 1)) | ((uint)(hash >> 48) << 16);
            if (seen.TryGetValue(key, out first))
            {
                second = name;
                break;
            }

            seen.Add(key, name);
        }

        Assert.IsNotNull(first, "the fixed collision corpus must contain a bucket and tag collision");
        Assert.IsNotNull(second);
        Assert.IsTrue(NamePool.TryGetOrAdd(block, first, out var firstId));
        Assert.IsTrue(NamePool.TryGetOrAdd(block, second, out var secondId));
        Assert.AreNotEqual(firstId, secondId);
        Assert.AreEqual(block.NameEntries[(int)firstId].HashTag, block.NameEntries[(int)secondId].HashTag);
        Assert.IsTrue(NamePool.TryFindOrdinal(block, first, out var foundFirst));
        Assert.IsTrue(NamePool.TryFindOrdinal(block, second, out var foundSecond));
        Assert.AreEqual(firstId, foundFirst);
        Assert.AreEqual(secondId, foundSecond);
        Assert.AreEqual(first, NamePool.Read(block, firstId).ToString());
        Assert.AreEqual(second, NamePool.Read(block, secondId).ToString());
        Assert.IsNull(NamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryGetOrAdd_CaseVariantsAreDistinctEntriesInOneChainWithOneTag()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "Makefile", out var upperId));
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "makefile", out var lowerId));
        Assert.AreNotEqual(upperId, lowerId);
        Assert.AreEqual(2u, block.NameEntryCount);
        Assert.AreEqual(block.NameEntries[(int)upperId].HashTag, block.NameEntries[(int)lowerId].HashTag);
        Assert.AreEqual(upperId, block.NameEntries[(int)lowerId].Next);
        Assert.AreEqual("Makefile", NamePool.Read(block, upperId).ToString());
        Assert.AreEqual("makefile", NamePool.Read(block, lowerId).ToString());
        Assert.IsTrue(NamePool.TryFindOrdinal(block, "makefile", out var foundLower));
        Assert.AreEqual(lowerId, foundLower);
        Assert.IsTrue(NamePool.TryFindOrdinal(block, "Makefile", out var foundUpper));
        Assert.AreEqual(upperId, foundUpper);
        Assert.IsFalse(NamePool.TryFindOrdinal(block, "MAKEFILE", out _));
        Assert.IsNull(NamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryGetOrAdd_TheSameSpellingReturnsTheSameId()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "Alpha", out var firstId));
        var used = block.NamePoolUsed;
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "Alpha", out var sameId));
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "beta", out var distinctId));
        Assert.AreEqual(firstId, sameId);
        Assert.AreNotEqual(firstId, distinctId);
        Assert.AreEqual(used + 4, block.NamePoolUsed);
        Assert.AreEqual(2u, block.NameEntryCount);
        Assert.IsFalse(NamePool.TryFindOrdinal(block, "missing", out _));
        Assert.IsNull(NamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryGetOrAdd_EmptyNameUsesTheReservedIdWithoutAdvancingStorage()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, [], out var nameId));
        Assert.AreEqual(0u, nameId);
        Assert.AreEqual(0u, block.NamePoolUsed);
        Assert.AreEqual(0u, block.NameEntryCount);
        Assert.AreEqual("", NamePool.Read(block, nameId).ToString());
    }

    [TestMethod]
    public void TryGetOrAdd_AsciiTakesOneBytePerUnitAndNonAsciiIsUtf16AtAnEvenOffset()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "a.txt", out var asciiId));
        Assert.AreEqual(5u, block.NamePoolUsed);
        Assert.IsFalse(NamePool.Read(block, asciiId).IsUtf16);
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "café", out var wideId));
        Assert.AreEqual(6u, block.NameEntries[(int)wideId].PoolOffsetBytes);
        Assert.AreEqual(14u, block.NamePoolUsed);
        Assert.IsTrue(NamePool.Read(block, wideId).IsUtf16);
        Assert.AreEqual("café", NamePool.Read(block, wideId).ToString());
        Assert.IsNull(NamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryGetOrAdd_AnUnpairedSurrogateNameIsStoredUtf16AndReadsBackExactly()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        const string name = "\uD800x\uDC00";
        Assert.IsTrue(NamePool.TryGetOrAdd(block, name, out var nameId));
        var view = NamePool.Read(block, nameId);
        Assert.IsTrue(view.IsUtf16);
        Assert.AreEqual(name, view.ToString());
        Assert.IsTrue(view.EqualsOrdinal(name));
        Assert.IsTrue(NamePool.TryFindOrdinal(block, name, out var foundId));
        Assert.AreEqual(nameId, foundId);
        Assert.IsNull(NamePool.CheckIntegrity(block));
    }

    [TestMethod]
    [DataRow("alpha.txt", false, 9)]
    [DataRow("café.txt", true, 16)]
    [DataRow("Σς.txt", true, 12)]
    public void TryGetOrAdd_StoresTheNarrowestWidthAndRoundTrips(string name, bool utf16, int bytes)
    {
        using var builder = new SyntheticBlockBuilder();
        var row = builder.AddRow(name, 0, RowFlags.InUse, 0, DateTime.UnixEpoch, 0);
        var block = builder.OpenForWriting();
        var view = NamePool.ReadRowName(block, row);
        Assert.AreEqual(utf16, view.IsUtf16);
        Assert.AreEqual(name.Length, view.Length);
        Assert.AreEqual((uint)bytes, block.NamePoolUsed);
        Assert.AreEqual(name, view.ToString());
        var copy = new char[name.Length];
        view.CopyTo(copy);
        Assert.AreEqual(name, new string(copy));
        Assert.IsTrue(view.EqualsOrdinal(name));
        Assert.IsFalse(view.EqualsOrdinal(name.ToUpperInvariant()));
        var rootBlock = new RootBlock(new VolumeKey('T'), block);
        try
        {
            Assert.AreEqual(name, FileEntry.Create(rootBlock, row).Name);
        }
        finally
        {
            rootBlock.ReleaseNowAsync().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TryGetOrAdd_AHitSucceedsWhenEntriesOrPoolAreFull(bool exhaustEntries)
    {
        using var builder = new SyntheticBlockBuilder(slotCapacity: exhaustEntries ? 1u : 8u,
            namePoolCapacity: exhaustEntries ? 128u : 4u);
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "four", out var firstId));
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "four", out var reusedId));
        Assert.AreEqual(firstId, reusedId);
        Assert.IsFalse(NamePool.TryGetOrAdd(block, "FOUR", out _));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TryGetOrAdd_AMissFailsWhenEntriesOrPoolAreFull(bool exhaustEntries)
    {
        using var builder = new SyntheticBlockBuilder(slotCapacity: exhaustEntries ? 1u : 8u,
            namePoolCapacity: exhaustEntries ? 128u : 4u);
        var block = builder.OpenForWriting();
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "four", out _));
        var used = block.NamePoolUsed;
        var count = block.NameEntryCount;
        Assert.IsFalse(NamePool.TryGetOrAdd(block, "another", out var rejectedId));
        Assert.AreEqual(0u, rejectedId);
        Assert.AreEqual(used, block.NamePoolUsed);
        Assert.AreEqual(count, block.NameEntryCount);

        var writer = new BlockWriter(block);
        Assert.IsFalse(writer.TryWriteRow(1, "another", new RowColumns(0, RowFlags.InUse, 0, 0, 0, 0)));
        Assert.IsTrue(block.Header.IsCapacityExhausted);
        Assert.IsNull(NamePool.CheckIntegrity(block));
    }

    [TestMethod]
    public void TryGetOrAdd_NameLongerThanTheLengthFieldIsRejected()
    {
        using var builder = new SyntheticBlockBuilder(namePoolCapacity: 100_000);
        var block = builder.OpenForWriting();
        var oversized = new string('x', NamePool.MaximumNameLengthUnits + 1);
        Assert.IsFalse(NamePool.TryGetOrAdd(block, oversized, out _));
        Assert.AreEqual(0u, block.NamePoolUsed);
        Assert.AreEqual(0u, block.NameEntryCount);
    }

    [TestMethod]
    [DataRow(uint.MaxValue)]
    [DataRow(uint.MaxValue - 1)]
    [DataRow(uint.MaxValue - 2)]
    [DataRow(uint.MaxValue - 3)]
    [DataRow(2147483648u)]
    [DataRow(3221225472u)]
    public void TryGetOrAdd_UntrustedHeaderCursorCannotRedirectWrites(uint usedBytes)
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        block.Header.NamePoolUsed = usedBytes;
        Assert.IsTrue(NamePool.TryGetOrAdd(block, "a", out var nameId));
        Assert.AreEqual(1u, nameId);
        Assert.AreEqual(1u, block.NamePoolUsed);
        Assert.AreEqual("a", NamePool.Read(block, nameId).ToString());
    }

    [TestMethod]
    public void Reopen_PreservesIdsAndSpellings()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        var upper = builder.AddRow("README.md", 0, RowFlags.InUse, 0, DateTime.UnixEpoch, 0);
        var lower = builder.AddRow("readme.md", 0, RowFlags.InUse, 0, DateTime.UnixEpoch, 0);
        var wide = builder.AddRow("café", 0, RowFlags.InUse, 0, DateTime.UnixEpoch, 0);
        builder.Complete(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));

        using var reopened = builder.OpenForReading(out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
        Assert.AreEqual("README.md", NamePool.ReadRowName(reopened, upper).ToString());
        Assert.AreEqual("readme.md", NamePool.ReadRowName(reopened, lower).ToString());
        Assert.AreEqual("café", NamePool.ReadRowName(reopened, wide).ToString());
        Assert.IsTrue(NamePool.TryFindOrdinal(reopened, "readme.md", out var foundId));
        Assert.AreEqual(reopened.Rows[(int)lower].NameId, foundId);
        Assert.IsNull(NamePool.CheckIntegrity(reopened));
    }

    [TestMethod]
    [DataRow(509u)]
    [DataRow(510u)]
    [DataRow(511u)]
    [DataRow(512u)]
    public void PoolRefusal_OfANtfsLengthName_LeavesAtMostTheExhaustionMargin(uint remaining)
    {
        const uint poolCapacity = 4096;
        var filler = new string('f', (int)(poolCapacity - remaining));
        using var utf16Block = TestBlocks.Create(slotCapacity: 64, namePoolCapacity: poolCapacity,
            nameEntryCapacity: 64);
        Assert.IsTrue(NamePool.TryGetOrAdd(utf16Block, filler, out _));
        var utf16Added = NamePool.TryGetOrAdd(utf16Block, "é" + new string('a', 254), out _);
        if (!utf16Added)
        {
            Assert.IsTrue(remaining <= BlockLayout.NamePoolExhaustionMarginBytes);
            Assert.AreEqual(poolCapacity - remaining, utf16Block.NamePoolUsed);
        }

        if (remaining >= 511)
        {
            Assert.IsTrue(utf16Added);
        }

        using var asciiBlock = TestBlocks.Create(slotCapacity: 64, namePoolCapacity: poolCapacity,
            nameEntryCapacity: 64);
        Assert.IsTrue(NamePool.TryGetOrAdd(asciiBlock, filler, out _));
        var asciiAdded = NamePool.TryGetOrAdd(asciiBlock, new string('b', 255), out _);
        if (!asciiAdded)
        {
            Assert.IsTrue(remaining <= BlockLayout.NamePoolExhaustionMarginBytes);
            Assert.AreEqual(poolCapacity - remaining, asciiBlock.NamePoolUsed);
        }

        if (remaining >= 511)
        {
            Assert.IsTrue(asciiAdded);
        }
    }

    [TestMethod]
    public void Restore_IntoABlockWithOnlyTheEntryCapacityDoubled_PreservesIdsAndIntegrity()
    {
        using var source = TestBlocks.Create(slotCapacity: 64, namePoolCapacity: 4096, nameEntryCapacity: 64);
        var writer = new BlockWriter(source);
        for (uint row = 0; row < 64; row++)
        {
            Assert.IsTrue(writer.TryWriteRow(row, $"name-{row}",
                new RowColumns(0, RowFlags.InUse, 0, 0, 0, 0)));
        }

        var snapshot = BlockRowSnapshot.Capture(source);
        using var target = TestBlocks.Create(slotCapacity: 64, namePoolCapacity: 4096, nameEntryCapacity: 128);
        snapshot.RestoreInto(target);

        Assert.AreEqual(BlockLayout.ComputeBucketCount(128), target.BucketCount);
        for (var row = 0; row < 64; row++)
        {
            Assert.AreEqual(source.Rows[row].NameId, target.Rows[row].NameId);
            Assert.AreEqual($"name-{row}", NamePool.ReadRowName(target, (uint)row).ToString());
        }

        Assert.IsNull(NamePool.CheckIntegrity(target));
        Assert.IsTrue(NamePool.TryGetOrAdd(target, "name-64", out var nameId));
        Assert.AreEqual(65u, nameId);
        Assert.AreEqual(65u, target.NameEntryCount);
    }

    [TestMethod]
    public void Growth_RestorePreservesIdsAndSpellings()
    {
        using var small = new SyntheticBlockBuilder(slotCapacity: 64, namePoolCapacity: 4096);
        var block = small.OpenForWriting();
        var writer = new BlockWriter(block);
        var spellings = new[] { "Makefile", "makefile", "café", "\uD800lone", "a.txt" };
        var columns = new RowColumns(0, RowFlags.InUse, 0, 0, 0, 0);
        for (var index = 0; index < spellings.Length; index++)
        {
            Assert.IsTrue(writer.TryWriteRow((uint)index, spellings[index], columns));
        }

        var snapshot = BlockRowSnapshot.Capture(block);
        using var large = new SyntheticBlockBuilder(driveLetter: 'U', slotCapacity: 8192, namePoolCapacity: 1 << 20);
        var target = large.OpenForWriting();
        snapshot.RestoreInto(target);

        Assert.AreNotEqual(block.BucketCount, target.BucketCount);
        for (var index = 0; index < spellings.Length; index++)
        {
            Assert.AreEqual(block.Rows[index].NameId, target.Rows[index].NameId);
            Assert.AreEqual(spellings[index], NamePool.ReadRowName(target, (uint)index).ToString());
            Assert.IsTrue(NamePool.TryFindOrdinal(target, spellings[index], out var foundId));
            Assert.AreEqual(target.Rows[index].NameId, foundId);
        }

        Assert.IsNull(NamePool.CheckIntegrity(target));
    }
}
