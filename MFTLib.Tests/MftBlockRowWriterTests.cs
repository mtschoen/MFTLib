using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftBlockRowWriterTests
{
    static readonly DateTime ModifiedUtc = new(2026, 9, 3, 12, 34, 56, DateTimeKind.Utc);

    [TestMethod]
    public void WriteBatches_MapsColumnsAtRecordNumbersAndReturnsHeaderTotals()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory, 999), ".", null),
            new(20, 5, new MftRecordFields(1, FileAttributes.Archive | FileAttributes.Hidden,
                5_000_000_000, ModifiedUtc.ToFileTimeUtc()), "snow\u2603.txt", null),
            new(21, 20, new MftRecordFields(0x8001), "unknown", null)
        ];

        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records],
            MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual(5u, block.Rows[20].ParentRow);
        Assert.AreEqual((uint)(FileAttributes.Archive | FileAttributes.Hidden), block.Rows[20].Attributes);
        Assert.AreEqual(5_000_000_000L, block.Rows[20].Size);
        Assert.AreEqual(ModifiedUtc.Ticks, block.Rows[20].ModifiedTicks);
        Assert.AreEqual(RowFlags.InUse, block.Rows[20].Flags);
        Assert.AreEqual("snow\u2603.txt", NamePool.ReadRowName(block, 20).ToString());
        Assert.AreEqual(5u, block.Rows[5].ParentRow);
        Assert.AreEqual(0L, block.Rows[5].Size);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.InUse | RowFlags.SizeUnknown, block.Rows[21].Flags);
        Assert.AreEqual(0L, block.Rows[21].Size);
        Assert.AreEqual(RowFlags.None, block.Rows[19].Flags);
        Assert.AreEqual(22L, result.RowCount);
        Assert.AreEqual(34L, result.NamePoolUsedBytes);
        Assert.AreEqual(block.Header.RowCount, result.RowCount);
        Assert.AreEqual(block.Header.NamePoolUsed, result.NamePoolUsedBytes);
        Assert.AreEqual(0L, result.SkippedRecordCount);
        Assert.IsFalse(result.CompactionNeeded);
        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void WriteBatches_CopiesSequenceNumberIntoItsRecordSlot()
    {
        using var block = CreateBlock();
        var record = new MftRecord(20, 5, new MftRecordFields(1, sequenceNumber: 37), "record", null);

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [[record]],
            MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual((ushort)37, block.SequenceNumbers[20]);
    }

    [TestMethod]
    public void WriteBatches_SlotCapacityOverflowIsCountedAndMarksCompaction()
    {
        using var block = CreateBlock();
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Record(32, "overflow"), Record(31, "last")]], MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount);
        Assert.AreEqual(32L, result.RowCount);
        Assert.AreEqual(8L, result.NamePoolUsedBytes);
        Assert.IsTrue(result.CompactionNeeded);
        Assert.IsTrue(block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public void WriteBatches_RecordNumberBeyondUInt32AndEmptyNamesAreSkipped()
    {
        using var block = CreateBlock();
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Record((ulong)uint.MaxValue + 1, "overflow"), Record(6, ""), Record(7, "kept")]],
            MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual(2L, result.SkippedRecordCount);
        Assert.AreEqual(8L, result.RowCount);
        Assert.AreEqual(8L, result.NamePoolUsedBytes);
        Assert.AreEqual(RowFlags.None, block.Rows[0].Flags);
        Assert.AreEqual(RowFlags.None, block.Rows[6].Flags);
        Assert.IsFalse(result.CompactionNeeded);
    }

    [TestMethod]
    public void WriteBatches_ParentRecordNumberBeyondUInt32IsSkipped()
    {
        // Both identifiers are 48-bit on disk. Truncating the parent instead of skipping
        // the record would silently point the child at an unrelated row rather than leave
        // it out of the block.
        using var block = CreateBlock();
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[RecordWithParent(8, (ulong)uint.MaxValue + 1, "orphan"), Record(9, "kept")]],
            MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount);
        Assert.AreEqual(RowFlags.None, block.Rows[8].Flags);
        Assert.AreEqual("kept", NamePool.ReadRowName(block, 9).ToString());
    }

    [TestMethod]
    public void WriteBatches_NamePoolOverflowContinuesWithRecordsThatFit()
    {
        using var block = CreateBlock(namePoolCapacity: 8);
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Record(5, "abc"), Record(6, "long"), Record(7, "z")]], MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount);
        Assert.AreEqual(8L, result.RowCount);
        Assert.AreEqual(8L, result.NamePoolUsedBytes);
        Assert.AreEqual(RowFlags.None, block.Rows[6].Flags);
        Assert.AreEqual("z", NamePool.ReadRowName(block, 7).ToString());
        Assert.IsTrue(result.CompactionNeeded);
    }

    [TestMethod]
    public void WriteBatches_WritesEachBatchBeforeRequestingTheNextAndReportsTransferCounts()
    {
        using var block = CreateBlock();
        var reports = new List<BlockWriteProgress>();
        var writer = new BlockWriter(block);

        IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            yield return [Record(5, ".")];
            Assert.AreEqual(".", NamePool.ReadRowName(writer.Block, 5).ToString());
            yield return [Record(20, "file"), Record(21, "")];
        }

        MftBlockRowWriter.WriteBatches(writer, Batches(), MftBlockRowFilter.Full,
            new DirectProgress(reports.Add), CancellationToken.None);

        Assert.AreEqual(2, reports.Count);
        Assert.AreEqual(new BlockWriteProgress(1, 2, null, null, BrokerScanPhase.Transferring), reports[0]);
        Assert.AreEqual(new BlockWriteProgress(2, 10, null, null, BrokerScanPhase.Transferring), reports[1]);
    }

    [TestMethod]
    public void WriteBatches_CancelledBeforeStartingDoesNotEnumerate()
    {
        using var block = CreateBlock();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationToken = cancellation.Token;
        var writer = new BlockWriter(block);

        IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            Assert.Fail("A cancelled scan must not request a batch.");
            yield break;
        }

        Assert.ThrowsException<OperationCanceledException>(() =>
            MftBlockRowWriter.WriteBatches(writer, Batches(), MftBlockRowFilter.Full, null, cancellationToken));
        Assert.AreEqual(0u, block.Header.RowCount);
        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void WriteBatches_CancelledAfterBatchLeavesLaterRowsUnwritten()
    {
        using var block = CreateBlock();
        using var cancellation = new CancellationTokenSource();
        var progress = new CancellationProgress(cancellation);
        var cancellationToken = cancellation.Token;
        var writer = new BlockWriter(block);

        Assert.ThrowsException<OperationCanceledException>(() => MftBlockRowWriter.WriteBatches(
            writer, [[Record(5, ".")], [Record(6, "file")]], MftBlockRowFilter.Full, progress, cancellationToken));

        Assert.AreEqual(6u, block.Header.RowCount);
        Assert.AreEqual(RowFlags.None, block.Rows[6].Flags);
        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void WriteBatches_EmptyInputReturnsExistingHeaderState()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(5, RowFlags.InUse, 0, 0, 0, 0)));
        writer.MarkCompactionNeeded();

        var result = MftBlockRowWriter.WriteBatches(writer, [], MftBlockRowFilter.Full, null, CancellationToken.None);

        Assert.AreEqual(new BlockWriteResult(6, 2, 0, true), result);
    }

    [TestMethod]
    public void WriteBatches_FullProfile_WritesRowsForFilesInUseAndDirectories()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory), "dir", null),
            new(10, 5, new MftRecordFields(1, FileAttributes.Normal, 100), "file.txt", null)
        ];

        var result = MftBlockRowWriter.WriteBatches(
            new BlockWriter(block), [records], new MftBlockRowFilter(BrokerScanProfile.Full, ["ignored.txt"]),
            null, CancellationToken.None);

        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.InUse, block.Rows[10].Flags);
        Assert.AreEqual(11L, result.RowCount);
        Assert.AreEqual(0L, result.SkippedRecordCount);
    }

    [TestMethod]
    public void WriteBatches_DirectoryIndexWithoutKeepNames_WritesOnlyDirectoriesAndSlotsForFilesAreUnused()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory), "dir", null),
            new(10, 5, new MftRecordFields(1, FileAttributes.Normal, 100), "file.txt", null)
        ];

        var result = MftBlockRowWriter.WriteBatches(
            new BlockWriter(block), [records], new MftBlockRowFilter(BrokerScanProfile.DirectoryIndex),
            null, CancellationToken.None);

        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.None, block.Rows[10].Flags);
        Assert.AreEqual(6L, result.RowCount);
        Assert.AreEqual(0L, result.SkippedRecordCount);
    }

    [TestMethod]
    public void WriteBatches_DirectoryIndexWithKeepName_PreservesNamedFileCaseInsensitively()
    {
        using var block = CreateBlock();
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory), "dir", null),
            new(10, 5, new MftRecordFields(1, FileAttributes.Normal, 100), ".GIT", null),
            new(11, 5, new MftRecordFields(1, FileAttributes.Normal, 200), "other.txt", null)
        ];

        var result = MftBlockRowWriter.WriteBatches(
            new BlockWriter(block), [records], new MftBlockRowFilter(BrokerScanProfile.DirectoryIndex, [".git"]),
            null, CancellationToken.None);

        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.InUse, block.Rows[10].Flags);
        Assert.AreEqual(RowFlags.None, block.Rows[11].Flags);
        Assert.AreEqual(11L, result.RowCount);
        Assert.AreEqual(0L, result.SkippedRecordCount);
    }

    [TestMethod]
    public void WriteBatches_FilteredRowsDoNotIncreaseSkippedRecordCountOrProgress()
    {
        using var block = CreateBlock();
        var reports = new List<BlockWriteProgress>();
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory), "dir", null),
            new(10, 5, new MftRecordFields(1, FileAttributes.Normal, 100), "filtered1.txt", null),
            new(11, 5, new MftRecordFields(1, FileAttributes.Normal, 200), "filtered2.txt", null)
        ];

        var result = MftBlockRowWriter.WriteBatches(
            new BlockWriter(block), [records], new MftBlockRowFilter(BrokerScanProfile.DirectoryIndex),
            new DirectProgress(reports.Add), CancellationToken.None);

        Assert.AreEqual(0L, result.SkippedRecordCount);
        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(1L, reports[0].RecordsProcessed);
    }

    [TestMethod]
    public void WriteBatches_UnknownProfile_ThrowsInvalidDataException()
    {
        using var block = CreateBlock();
        var writer = new BlockWriter(block);
        var filter = new MftBlockRowFilter((BrokerScanProfile)99);

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            MftBlockRowWriter.WriteBatches(writer, [], filter, null, CancellationToken.None));
        StringAssert.Contains(exception.Message, "99");
    }

    static MftRecord Record(ulong recordNumber, string name)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(1), name, null);
    }

    static MftRecord RecordWithParent(ulong recordNumber, ulong parentRecordNumber, string name)
    {
        return new MftRecord(recordNumber, parentRecordNumber, new MftRecordFields(1), name, null);
    }

    static BlockFile CreateBlock(uint namePoolCapacity = 256)
    {
        return BlockFile.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"mft-block-rows-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 32,
            NamePoolCapacity = namePoolCapacity,
            DeleteOnClose = true
        });
    }

    sealed class DirectProgress(Action<BlockWriteProgress> handler) : IProgress<BlockWriteProgress>
    {
        public void Report(BlockWriteProgress value) => handler(value);
    }

    sealed class CancellationProgress(CancellationTokenSource cancellation) : IProgress<BlockWriteProgress>
    {
        public void Report(BlockWriteProgress value) => cancellation.Cancel();
    }
}
