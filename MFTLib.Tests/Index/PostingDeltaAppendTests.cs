using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A journal name change after the posting base is built appends one delta entry before the row descriptor is
///     published, and nothing else touches the postings (issue 859, design sections 4.1 and 4.2).
/// </summary>
[TestClass]
public sealed class PostingDeltaAppendTests : IDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);

    static readonly RowColumns DirectoryColumns =
        new(0, RowFlags.InUse | RowFlags.Directory, 16, 0, Moment.Ticks, 0);

    static readonly RowColumns FileColumns = new(0, RowFlags.InUse, 0, 1, Moment.Ticks, 0);

    readonly TempDirectory _directory = new("mftlib-posting-delta");
    BlockFile _block = null!;
    BlockWriter _writer = null!;

    public void Dispose()
    {
        _block.Dispose();
        _directory.Dispose();
    }

    void CreateBlock(uint slotCapacity = 64, uint deltaCapacity = 16, bool complete = true)
    {
        _block = BlockFile.Create(TestBlocks.Options(Path.Combine(_directory.Path, "T-0BADF00D.mlix"),
            ProducerKind.Mft, slotCapacity, namePoolCapacity: 65536) with
        { DeltaCapacity = deltaCapacity });
        _writer = new BlockWriter(_block);
        Assert.IsTrue(_writer.TryWriteRow(0, "", DirectoryColumns));
        Assert.IsTrue(_writer.TryWriteRow(1, "base.txt", FileColumns));
        Assert.IsTrue(_writer.TryWriteRow(2, "base.txt", FileColumns));
        if (complete)
        {
            _writer.Complete(Moment, null);
        }
    }

    uint NameIdOf(string name)
    {
        Assert.IsTrue(SyntheticNamePool.TryFindOrdinal(_block, name, out var nameId));
        return nameId;
    }

    [TestMethod]
    public void CreateAfterComplete_AppendsOneEntryBeforeTheDescriptor()
    {
        CreateBlock();

        Assert.IsTrue(_writer.TryWriteRow(5, "fresh.txt", FileColumns));

        var nameId = NameIdOf("fresh.txt");
        Assert.AreEqual(1u, _block.DeltaEntryCount);
        Assert.AreEqual(1u, _block.Header.DeltaEntryCount);
        var entry = _block.DeltaEntries[1];
        Assert.AreEqual(5u, entry.Row);
        Assert.AreEqual(nameId, entry.NameId);
        Assert.AreEqual(0u, entry.Next);
        Assert.AreEqual(1u, _block.DeltaHeads[(int)(nameId & (_block.BucketCount - 1))]);
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
    }

    [TestMethod]
    public void CreateBeforeComplete_AppendsNothing()
    {
        CreateBlock(complete: false);

        Assert.IsTrue(_writer.TryWriteRow(5, "fresh.txt", FileColumns));

        Assert.AreEqual(0u, _block.DeltaEntryCount);
    }

    [TestMethod]
    public void CreateOnAReusedSlotWithTheSameName_AppendsNothing()
    {
        CreateBlock();
        _writer.MarkTombstone(1);

        Assert.IsTrue(_writer.TryWriteRow(1, "base.txt", FileColumns));

        Assert.AreEqual(0u, _block.DeltaEntryCount);
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
    }

    [TestMethod]
    public void CreateOnAReusedSlotWithADifferentName_AppendsOneEntry()
    {
        CreateBlock();
        _writer.MarkTombstone(1);

        Assert.IsTrue(_writer.TryWriteRow(1, "other.txt", FileColumns));

        Assert.AreEqual(1u, _block.DeltaEntryCount);
        Assert.AreEqual(1u, _block.DeltaEntries[1].Row);
        Assert.AreEqual(NameIdOf("other.txt"), _block.DeltaEntries[1].NameId);
    }

    [TestMethod]
    public void RewriteWithTheSameName_AppendsNothing()
    {
        CreateBlock();

        Assert.IsTrue(_writer.TryWriteRow(1, "base.txt", FileColumns with { Size = 99 }));

        Assert.AreEqual(0u, _block.DeltaEntryCount);
    }

    [TestMethod]
    public void RenameToANewSpelling_AppendsOneEntry()
    {
        CreateBlock();

        Assert.IsTrue(_writer.TryRenameRow(1, "renamed.txt", 0));

        Assert.AreEqual(1u, _block.DeltaEntryCount);
        Assert.AreEqual(1u, _block.DeltaEntries[1].Row);
        Assert.AreEqual(NameIdOf("renamed.txt"), _block.DeltaEntries[1].NameId);
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
    }

    [TestMethod]
    public void CaseOnlyRename_AppendsOneEntry()
    {
        CreateBlock();

        Assert.IsTrue(_writer.TryRenameRow(1, "BASE.TXT", 0));

        Assert.AreEqual(1u, _block.DeltaEntryCount);
        Assert.AreEqual(NameIdOf("BASE.TXT"), _block.DeltaEntries[1].NameId);
    }

    [TestMethod]
    public void RenameAwayAndBack_AppendsAnEntryForEachChangeAndLeavesTheBaseAlone()
    {
        CreateBlock();
        var baseRowCount = _block.PostingBaseRowCount;

        Assert.IsTrue(_writer.TryRenameRow(1, "away.txt", 0));
        Assert.IsTrue(_writer.TryRenameRow(1, "base.txt", 0));

        Assert.AreEqual(2u, _block.DeltaEntryCount);
        Assert.AreEqual(NameIdOf("base.txt"), _block.DeltaEntries[2].NameId);
        Assert.AreEqual(baseRowCount, _block.PostingBaseRowCount);
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
    }

    [TestMethod]
    public void ParentOnlyMove_AppendsNothing()
    {
        CreateBlock();

        Assert.IsTrue(_writer.TryRenameRow(1, "base.txt", 2));

        Assert.AreEqual(0u, _block.DeltaEntryCount);
    }

    [TestMethod]
    public void TombstoneAndDetach_AppendNothing()
    {
        CreateBlock();

        _writer.MarkTombstone(1);
        _writer.DetachRow(2);

        Assert.AreEqual(0u, _block.DeltaEntryCount);
    }

    [TestMethod]
    public void NamesThatShareAHead_ChainNewestFirstWithSmallerNextIndices()
    {
        CreateBlock(slotCapacity: 2200, deltaCapacity: 4096);
        var bucketCount = _block.BucketCount;
        var row = 3u;
        for (var index = 0; index <= bucketCount + 1; index++, row++)
        {
            Assert.IsTrue(_writer.TryWriteRow(row, $"name-{index}", FileColumns));
        }

        var entries = _block.DeltaEntries;
        var head = _block.DeltaHeads[(int)(entries[1].NameId & (bucketCount - 1))];
        Assert.IsGreaterThan(1u, head, "a later name landed in the first name's head");
        Assert.IsLessThan(head, entries[(int)head].Next);
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
    }

    [TestMethod]
    public void ExhaustedDelta_RefusesTheWriteSetsTheFlagAndKeepsTheRowUntouched()
    {
        CreateBlock(deltaCapacity: 2);
        Assert.IsTrue(_writer.TryWriteRow(5, "one.txt", FileColumns));
        Assert.IsTrue(_writer.TryWriteRow(6, "two.txt", FileColumns));
        Assert.IsFalse(_block.Header.IsCapacityExhausted);

        Assert.IsFalse(_writer.TryWriteRow(7, "three.txt", FileColumns));

        Assert.IsTrue(_block.Header.IsCapacityExhausted);
        Assert.AreEqual(2u, _block.DeltaEntryCount);
        Assert.AreEqual(0u, FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in _block.Rows[7])));
        Assert.AreEqual(7u, _block.RowCount, "the refused row was never published");
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
    }

    [TestMethod]
    public void ExhaustedDelta_RefusesARenameAndKeepsTheOldName()
    {
        CreateBlock(deltaCapacity: 1);
        Assert.IsTrue(_writer.TryRenameRow(1, "first.txt", 0));

        Assert.IsFalse(_writer.TryRenameRow(2, "second.txt", 0));

        Assert.IsTrue(_block.Header.IsCapacityExhausted);
        Assert.AreEqual("base.txt", NamePool.ReadRowName(_block, 2).ToString());
        Assert.AreEqual(1u, _block.DeltaEntryCount);
    }

    [TestMethod]
    public void ExhaustedDelta_StillAcceptsWritesThatNeedNoEntry()
    {
        CreateBlock(deltaCapacity: 1);
        Assert.IsTrue(_writer.TryWriteRow(5, "one.txt", FileColumns));

        Assert.IsTrue(_writer.TryWriteRow(1, "base.txt", FileColumns with { Size = 7 }));
        Assert.IsTrue(_writer.TryRenameRow(1, "base.txt", 2));

        Assert.IsFalse(_block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void GrowthMinimum_AfterDeltaExhaustionDoublesOnlyTheDeltaCapacity()
    {
        CreateBlock(deltaCapacity: 1);
        Assert.IsTrue(_writer.TryWriteRow(5, "one.txt", FileColumns));
        Assert.IsFalse(_writer.TryWriteRow(6, "two.txt", FileColumns));

        var minimum = BlockLayout.GrowthMinimum(_block.Header);

        Assert.AreEqual(2u, minimum.DeltaCapacity);
        Assert.AreEqual(0u, minimum.SlotCapacity);
        Assert.AreEqual(0u, minimum.NameEntryCapacity);
        Assert.AreEqual(0u, minimum.NamePoolCapacity);
    }
}
