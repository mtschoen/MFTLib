using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The copier moves a block's used regions into a larger mapped block without staging them on the managed
///     heap, carrying the header fields that describe the data and nothing that describes the capacity.
/// </summary>
[TestClass]
public sealed class BlockCopierTests
{
    const uint SourceSlots = 64;
    static readonly DateTime Moment = new(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
    static readonly RowColumns FileColumns = new(0, RowFlags.InUse, 0, 1, Moment.Ticks, 0);

    static BlockFile CreateSource(bool complete, bool exhausted = false)
    {
        var block = TestBlocks.Create(producer: ProducerKind.Mft, slotCapacity: SourceSlots, namePoolCapacity: 4096,
            rootRow: 5, cacheTag: new CacheTag("TEST", 7), scanShape: 0x1122334455667788);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(0, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 0)));
        Assert.IsTrue(writer.TryWriteRow(6, "Makefile", FileColumns));
        Assert.IsTrue(writer.TryWriteRow(7, "makefile", FileColumns));
        Assert.IsTrue(writer.TryWriteRow(8, "caf\u00E9.txt", FileColumns));
        Assert.IsTrue(writer.TryWriteRow(9, "gone.txt", FileColumns));
        Assert.IsTrue(writer.TryWriteRow(10, "freed.txt", new RowColumns(0, RowFlags.None, 0, 0, 0, 0)));
        writer.MarkTombstone(9);
        block.SequenceNumbers[6] = 3;
        block.SequenceNumbers[8] = 9;
        writer.SetJournalCursor(0xABCDEF, 4242);
        writer.SetAccessDeniedSubtreeCount(2);
        if (complete)
        {
            writer.Complete(Moment, null);
            Assert.IsTrue(writer.TryWriteRow(20, "after.txt", FileColumns));
            writer.BumpGeneration();
        }

        if (exhausted)
        {
            writer.MarkCapacityExhausted();
        }

        return block;
    }

    static void AssertSameData(BlockFile source, BlockFile target)
    {
        Assert.AreEqual(source.RowCount, target.RowCount);
        for (var row = 0; row < source.RowCount; row++)
        {
            Assert.AreEqual(source.Rows[row], target.Rows[row], $"row {row}");
            Assert.AreEqual(source.SequenceNumbers[row], target.SequenceNumbers[row], $"sequence {row}");
            if (source.Rows[row].NameId != 0)
            {
                Assert.AreEqual(NamePool.ReadRowName(source, (uint)row).ToString(),
                    NamePool.ReadRowName(target, (uint)row).ToString(), $"name {row}");
            }
        }

        Assert.AreEqual(source.NameEntryCount, target.NameEntryCount);
        Assert.AreEqual(source.NamePoolUsed, target.NamePoolUsed);
        Assert.IsNull(NamePoolIntegrity.Check(target));
        for (uint nameId = 1; nameId <= source.NameEntryCount; nameId++)
        {
            var spelling = NamePool.Read(source, nameId).ToString();
            Assert.IsTrue(NamePool.TryFindOrdinal(target, spelling, out var found));
            Assert.AreEqual(nameId, found);
        }
    }

    [TestMethod]
    [DataRow(false, DisplayName = "EntryCapacityUnchanged")]
    [DataRow(true, DisplayName = "EntryCapacityChanged")]
    public void Copy_IntoALargerBlock_KeepsRowsSequenceNumbersAndNames(bool changeEntryCapacity)
    {
        using var source = CreateSource(complete: false);
        using var target = TestBlocks.Create(slotCapacity: 4 * SourceSlots, namePoolCapacity: 16384,
            nameEntryCapacity: changeEntryCapacity ? 40_000u : SourceSlots);
        Assert.AreEqual(!changeEntryCapacity, source.BucketCount == target.BucketCount);

        BlockCopier.Copy(source, target, buildPostings: false, complete: false);

        AssertSameData(source, target);
        Assert.IsFalse(target.Header.IsComplete);
        Assert.IsFalse(target.PostingsBuilt);
    }

    [TestMethod]
    public void Copy_CarriesEveryDescribedHeaderFieldAndKeepsTheGeneration()
    {
        using var source = CreateSource(complete: true);
        using var target = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);

        BlockCopier.Copy(source, target, buildPostings: true, complete: true);

        var expected = source.Header;
        var actual = target.Header;
        Assert.AreEqual(expected.ProducerKind, actual.ProducerKind);
        Assert.AreEqual(expected.VolumeSerial, actual.VolumeSerial);
        Assert.AreEqual(expected.RootRow, actual.RootRow);
        Assert.AreEqual(expected.ScanTimestampTicks, actual.ScanTimestampTicks);
        Assert.AreEqual(expected.UsnJournalIdentifier, actual.UsnJournalIdentifier);
        Assert.AreEqual(expected.UsnNextUsn, actual.UsnNextUsn);
        Assert.AreEqual(expected.Generation, actual.Generation);
        Assert.AreEqual(expected.LiveRowCount, actual.LiveRowCount);
        Assert.AreEqual(expected.CacheTagFourCc, actual.CacheTagFourCc);
        Assert.AreEqual(expected.CacheTagVersion, actual.CacheTagVersion);
        Assert.AreEqual(expected.ScanShape, actual.ScanShape);
        Assert.AreEqual(expected.AccessDeniedSubtreeCount, actual.AccessDeniedSubtreeCount);
        Assert.AreEqual(expected.RowCount, actual.RowCount);
        Assert.AreEqual(expected.NamePoolUsed, actual.NamePoolUsed);
        Assert.AreEqual(expected.NameEntryCount, actual.NameEntryCount);
        Assert.AreEqual(source.Generation, target.Generation);
        Assert.AreEqual(256u, actual.SlotCapacity);
        Assert.IsTrue(actual.IsComplete);
        Assert.IsFalse(actual.IsCapacityExhausted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Copy_CarriesTheCapacityExhaustedBitOnlyWhenTheSourceHadIt(bool exhausted)
    {
        using var source = CreateSource(complete: false, exhausted);
        using var target = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);

        BlockCopier.Copy(source, target, buildPostings: false, complete: false);

        Assert.AreEqual(exhausted, target.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void Copy_WithAPostingBuild_RebuildsTheBaseAndEmptiesTheDelta()
    {
        using var source = CreateSource(complete: true);
        Assert.IsGreaterThan(0u, source.DeltaEntryCount);
        using var target = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);

        BlockCopier.Copy(source, target, buildPostings: true, complete: true);

        Assert.IsTrue(target.PostingsBuilt);
        Assert.AreEqual(0u, target.DeltaEntryCount);
        Assert.AreEqual(target.NameEntryCount, target.PostingBaseNameCount);
        Assert.IsTrue(NamePool.TryFindOrdinal(target, "after.txt", out var nameId));
        var offsets = target.PostingOffsets;
        Assert.AreEqual(1u, offsets[(int)nameId + 1] - offsets[(int)nameId]);
        Assert.AreEqual(20u, target.PostingRows[(int)offsets[(int)nameId]]);
    }

    [TestMethod]
    public void Copy_TombstonedAndFreedRowsReadTheSame()
    {
        using var source = CreateSource(complete: false);
        using var target = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);

        BlockCopier.Copy(source, target, buildPostings: false, complete: false);

        Assert.AreEqual(RowFlags.None, target.Rows[10].Flags & RowFlags.InUse);
        Assert.AreEqual(source.Rows[9].Flags, target.Rows[9].Flags);
        Assert.AreEqual("gone.txt", NamePool.ReadRowName(target, 9).ToString());
        Assert.AreEqual("freed.txt", NamePool.ReadRowName(target, 10).ToString());
    }

    [TestMethod]
    public void Copy_RefusesCompletionWithoutAPostingBuild()
    {
        using var source = CreateSource(complete: false);
        using var target = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);

        Assert.Throws<ArgumentException>(() => BlockCopier.Copy(source, target, buildPostings: false, complete: true));
    }

    [TestMethod]
    public void Copy_RefusesATargetThatIsTooSmallOrNotEmpty()
    {
        using var source = CreateSource(complete: false);
        using var small = TestBlocks.Create(slotCapacity: 8, namePoolCapacity: 16384, nameEntryCapacity: 64);
        using var crowded = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);
        Assert.IsTrue(new BlockWriter(crowded).TryWriteRow(0, "x", FileColumns));

        Assert.Throws<ArgumentException>(() => BlockCopier.Copy(source, small, buildPostings: false, complete: false));
        Assert.Throws<ArgumentException>(() => BlockCopier.Copy(source, crowded, buildPostings: false, complete: false));
        Assert.Throws<ArgumentException>(() => BlockCopier.Copy(source, source, buildPostings: false, complete: false));
    }

    [TestMethod]
    public void Copy_ObservesCancellationBeforeCopying()
    {
        using var source = CreateSource(complete: false);
        using var target = TestBlocks.Create(slotCapacity: 256, namePoolCapacity: 16384);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BlockCopier.Copy(source, target, buildPostings: true, complete: true, cancelled.Token));

        Assert.AreEqual(0u, target.RowCount);
        Assert.IsFalse(target.Header.IsComplete);
    }
}
