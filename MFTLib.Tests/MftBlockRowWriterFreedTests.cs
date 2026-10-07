using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Freed MFT records become deleted rows, and a freed row keeps its parent only when its whole parent chain
///     verifies; every other freed row is detached.
/// </summary>
[TestClass]
public class MftBlockRowWriterFreedTests
{
    const ushort RootSequence = 5;
    static readonly MftBlockRowFilter IncludingFreed = new(BrokerScanProfile.Full, IncludeFreed: true);

    [TestMethod]
    public void FreedRow_HasTheFlagsAJournalDeleteLeavesAndIsNotLive()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Live(20, 5, "live.txt", sequence: 3, parentSequence: RootSequence),
            Freed(21, 5, "freed.txt", sequence: 4, parentSequence: RootSequence),
            Freed(22, 5, "freed-dir", sequence: 9, parentSequence: RootSequence, isDirectory: true)
        ];

        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, block.Rows[21].Flags);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone | RowFlags.Directory, block.Rows[22].Flags);
        Assert.IsTrue(block.Rows[21].IsDeleted);
        Assert.AreEqual((ushort)4, block.SequenceNumbers[21]);
        Assert.AreEqual("freed.txt", NamePool.ReadRowName(block, 21).ToString());
        Assert.AreEqual(2u, block.Header.LiveRowCount, "Only the root and the live file are live.");
        Assert.AreEqual(23L, result.RowCount);
        Assert.AreEqual(0L, result.SkippedRecordCount);
    }

    [TestMethod]
    public void LiveRows_AreUnchangedWhenFreedRowsAreIncluded()
    {
        using var block = CreateBlock();

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [[Root(), Live(20, 5, "a.txt", 1, RootSequence)]],
            IncludingFreed, null, CancellationToken.None);

        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.InUse, block.Rows[20].Flags);
        Assert.AreEqual(5u, block.Rows[20].ParentRow);
    }

    [TestMethod]
    public void FreedRecord_WithoutTheOption_GetsNoRowAndIsNotCounted()
    {
        using var block = CreateBlock();
        MftRecord[] records = [Root(), Freed(21, 5, "freed.txt", 4, RootSequence)];

        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], MftBlockRowFilter.Full, null,
            CancellationToken.None);

        Assert.AreEqual(RowFlags.None, block.Rows[21].Flags);
        Assert.AreEqual(6L, result.RowCount);
        Assert.AreEqual(0L, result.SkippedRecordCount);
        Assert.AreEqual(1u, block.Header.LiveRowCount);
    }

    [TestMethod]
    public void DirectoryIndexProfile_KeepsFreedDirectoriesAndNamedFreedFilesOnly()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Freed(12, 5, "freed-dir", 14, RootSequence, isDirectory: true),
            Freed(13, 12, "ignored.txt", 14, 14),
            Freed(14, 12, "KEEP.txt", 14, 14)
        ];

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records],
            new MftBlockRowFilter(BrokerScanProfile.DirectoryIndex, ["keep.txt"], IncludeFreed: true), null,
            CancellationToken.None);

        Assert.IsTrue(block.Rows[12].IsDeleted);
        Assert.AreEqual(RowFlags.None, block.Rows[13].Flags);
        Assert.IsTrue(block.Rows[14].IsDeleted);
        Assert.AreEqual(12u, block.Rows[14].ParentRow, "A named freed file under a verified directory keeps its parent.");
    }

    [TestMethod]
    public void TrustedFreedRow_KeepsItsParentAndUntrustedFreedRowIsDetached()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Live(12, 5, "dir", sequence: 6, parentSequence: RootSequence, isDirectory: true),
            Freed(13, 12, "trusted.txt", 14, parentSequence: 6),
            Freed(14, 12, "stale.txt", 15, parentSequence: 5)
        ];

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(12u, block.Rows[13].ParentRow);
        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[14].ParentRow);
        Assert.AreEqual("stale.txt", NamePool.ReadRowName(block, 14).ToString());
        Assert.AreEqual(5u, block.Rows[12].ParentRow, "A live row is never detached.");
    }

    [TestMethod]
    public void TrustRule_ParentArrivingInALaterBatch_StillVerifiesTheChild()
    {
        using var block = CreateBlock();
        var freedFile = Freed(13, 12, "early.txt", 14, parentSequence: 6);
        var directory = Live(12, 5, "dir", 6, RootSequence, isDirectory: true);

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [[freedFile], [directory], [Root()]], IncludingFreed,
            null, CancellationToken.None);

        Assert.AreEqual(12u, block.Rows[13].ParentRow);
    }

    // Each case builds root -> directory 12 -> freed file 13 and changes one fact of the chain.
    [TestMethod]
    [DataRow(true, (ushort)6, (ushort)6, true, DisplayName = "live parent, equal sequence")]
    [DataRow(true, (ushort)6, (ushort)5, false, DisplayName = "live parent, stored one higher than referenced")]
    [DataRow(true, (ushort)6, (ushort)7, false, DisplayName = "live parent, referenced above stored")]
    [DataRow(false, (ushort)14, (ushort)14, true, DisplayName = "freed parent, equal sequence")]
    [DataRow(false, (ushort)14, (ushort)13, true, DisplayName = "freed parent, stored one higher")]
    [DataRow(false, (ushort)14, (ushort)12, false, DisplayName = "freed parent, stored two higher")]
    [DataRow(false, (ushort)14, (ushort)15, false, DisplayName = "freed parent, referenced above stored")]
    [DataRow(false, (ushort)0, ushort.MaxValue, true, DisplayName = "freed parent, sequence wraps around")]
    [DataRow(true, (ushort)0, ushort.MaxValue, false, DisplayName = "live parent never wraps")]
    public void TrustRule_ParentSequence(bool parentLive, ushort storedSequence, ushort referencedSequence,
        bool trusted)
    {
        using var block = CreateBlock();
        var directory = parentLive
            ? Live(12, 5, "dir", storedSequence, RootSequence, isDirectory: true)
            : Freed(12, 5, "dir", storedSequence, RootSequence, isDirectory: true);

        MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Root(), directory, Freed(13, 12, "file.txt", 20, referencedSequence)]], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(trusted ? 12u : BlockLayout.DetachedParentRow, block.Rows[13].ParentRow);
    }

    [TestMethod]
    public void TrustRule_FinalHopMustMatchTheRootSequence()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Freed(12, 5, "dir", 14, parentSequence: RootSequence + 1, isDirectory: true),
            Freed(13, 12, "file.txt", 14, parentSequence: 14)
        ];

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[12].ParentRow);
        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[13].ParentRow,
            "A file under a directory that does not reach the root does not reach it either.");
    }

    [TestMethod]
    public void TrustRule_MissingOrNonDirectoryParentIsUntrusted()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Live(10, 5, "plain.txt", 3, RootSequence),
            Freed(13, 29, "no-such-parent.txt", 14, 0),
            Freed(14, 10, "under-a-file.txt", 14, 3),
            Freed(15, 31, "beyond-the-rows.txt", 14, 0)
        ];

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[13].ParentRow);
        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[14].ParentRow);
        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[15].ParentRow);
    }

    [TestMethod]
    public void TrustRule_ParentChainCycle_IsUntrustedForEveryMember()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Freed(12, 13, "a", 14, parentSequence: 14, isDirectory: true),
            Freed(13, 12, "b", 14, parentSequence: 14, isDirectory: true),
            Freed(14, 12, "leaf.txt", 14, parentSequence: 14)
        ];

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[12].ParentRow);
        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[13].ParentRow);
        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[14].ParentRow);
    }

    [TestMethod]
    public void TrustRule_ASelfParentedFreedRecordIsUntrusted()
    {
        using var block = CreateBlock();

        MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Root(), Freed(12, 12, "loop", 14, parentSequence: 14, isDirectory: true)]], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[12].ParentRow);
    }

    [TestMethod]
    public void TrustRule_AFreedRootRecordIsNeverTrusted()
    {
        using var block = CreateBlock();
        var freedRoot = new MftRecord(5, 5, new MftRecordFields(2, FileAttributes.Directory, 0, 0, RootSequence,
            RootSequence), ".", null);

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [[freedRoot]], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[5].ParentRow);
    }

    [TestMethod]
    public void TrustRule_RootWithoutAStoredNameStillVerifiesTheFinalHop()
    {
        using var block = CreateBlock();
        var namelessRoot = new MftRecord(5, 5, new MftRecordFields(3, FileAttributes.Directory, 0, 0, RootSequence),
            "", null);
        MftRecord[] records =
        [
            namelessRoot,
            Freed(12, 5, "dir", 14, parentSequence: RootSequence, isDirectory: true),
            Freed(13, 12, "file.txt", 14, parentSequence: 14)
        ];

        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount, "The nameless root gets no row.");
        Assert.AreEqual(5u, block.Rows[12].ParentRow);
        Assert.AreEqual(12u, block.Rows[13].ParentRow);
    }

    [TestMethod]
    public void TrustRule_WithoutAnyRootRecordNothingIsTrusted()
    {
        using var block = CreateBlock();

        MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Freed(12, 5, "dir", 14, parentSequence: RootSequence, isDirectory: true)]], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[12].ParentRow);
    }

    // 127 freed directories and the freed file make 128 components, which still resolve; one more does not.
    [TestMethod]
    [DataRow(127, true)]
    [DataRow(128, false)]
    public void TrustRule_ComponentLimit(int directoryCount, bool trusted)
    {
        using var block = CreateBlock(slotCapacity: 256, namePoolCapacity: 4096);
        var records = new List<MftRecord> { Root() };
        for (var index = 0; index < directoryCount; index++)
        {
            var recordNumber = (ulong)(20 + index);
            records.Add(Freed(recordNumber, index == 0 ? 5 : recordNumber - 1, "d", 14,
                index == 0 ? RootSequence : (ushort)14, isDirectory: true));
        }

        records.Add(Freed(13, (ulong)(19 + directoryCount), "file.txt", 14, parentSequence: 14));

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(trusted ? (uint)(19 + directoryCount) : BlockLayout.DetachedParentRow,
            block.Rows[13].ParentRow);
    }

    // 109 names of 300 units rebuild to 32,700 units plus 108 separators, past the 32,767-unit path limit.
    [TestMethod]
    [DataRow(108, true)]
    [DataRow(109, false)]
    public void TrustRule_PathLengthLimit(int componentCount, bool trusted)
    {
        using var block = CreateBlock(slotCapacity: 256, namePoolCapacity: 65536);
        var longName = new string('n', 300);
        var records = new List<MftRecord> { Root() };
        for (var index = 0; index < componentCount - 1; index++)
        {
            var recordNumber = (ulong)(20 + index);
            records.Add(Freed(recordNumber, index == 0 ? 5 : recordNumber - 1, longName, 14,
                index == 0 ? RootSequence : (ushort)14, isDirectory: true));
        }

        records.Add(Freed(13, (ulong)(19 + componentCount - 1), longName, 14, parentSequence: 14));

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        var expectedUnits = (300L * componentCount) + (componentCount - 1);
        Assert.AreEqual(expectedUnits <= FreedRowTrust.MaximumPathUnits, trusted,
            "The case table agrees with the limit it exercises.");
        Assert.AreEqual(trusted ? (uint)(19 + componentCount - 1) : BlockLayout.DetachedParentRow,
            block.Rows[13].ParentRow);
    }

    [TestMethod]
    public void DetachedRows_DoNotChangeTheVerdictOfTheirNeighbours()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            Root(),
            Freed(12, 5, "bad-dir", 14, parentSequence: 99, isDirectory: true),
            Freed(13, 5, "good-dir", 14, parentSequence: RootSequence, isDirectory: true),
            Freed(14, 13, "good.txt", 14, parentSequence: 14)
        ];

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records], IncludingFreed, null,
            CancellationToken.None);

        Assert.AreEqual(BlockLayout.DetachedParentRow, block.Rows[12].ParentRow);
        Assert.AreEqual(5u, block.Rows[13].ParentRow);
        Assert.AreEqual(13u, block.Rows[14].ParentRow);
    }

    static MftRecord Root() => Live(5, 5, ".", RootSequence, RootSequence, isDirectory: true);

    static MftRecord Live(ulong recordNumber, ulong parent, string name, ushort sequence, ushort parentSequence,
        bool isDirectory = false) =>
        new(recordNumber, parent, new MftRecordFields((ushort)(isDirectory ? 3 : 1),
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal, 0, 0, sequence, parentSequence), name,
            null);

    static MftRecord Freed(ulong recordNumber, ulong parent, string name, ushort sequence, ushort parentSequence,
        bool isDirectory = false) =>
        new(recordNumber, parent, new MftRecordFields((ushort)(isDirectory ? 2 : 0),
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal, 0, 0, sequence, parentSequence), name,
            null);

    static BlockFile CreateBlock(uint slotCapacity = 32, uint namePoolCapacity = 1024)
    {
        return BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"mft-block-freed-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity,
            DeleteOnClose = true
        });
    }
}
