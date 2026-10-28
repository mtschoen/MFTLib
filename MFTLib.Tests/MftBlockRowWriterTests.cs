using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class MftBlockRowWriterTests : IDisposable
{
    static readonly DateTime ModifiedUtc = new(2026, 9, 3, 12, 34, 56, DateTimeKind.Utc);
    TempDirectory _directory = null!;
    string _blockPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-row-writer");
        _blockPath = Path.Combine(_directory.Path, "block.bin");
    }

    [TestCleanup]
    public void Cleanup() => _directory.Dispose();

    public void Dispose() => Cleanup();

    [TestMethod]
    public void WriteBatches_MapsColumnsAtRecordNumbersAndReturnsHeaderTotals()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory, 999), "."),
            new(20, 5, new MftRecordFields(1, FileAttributes.Archive | FileAttributes.Hidden,
                5_000_000_000, ModifiedUtc.ToFileTimeUtc()), "snow\u2603.txt"),
            new(21, 20, new MftRecordFields(0x8001), "unknown")
        ];

        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block), [records],
            new MftScanOptions(), null, CancellationToken.None);

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
        Assert.AreEqual(27L, result.NamePoolUsedBytes); // ASCII root, alignment byte, UTF-16 name, ASCII name.
        Assert.AreEqual(block.Header.RowCount, result.RowCount);
        Assert.AreEqual(block.Header.NamePoolUsed, result.NamePoolUsedBytes);
        Assert.AreEqual(0L, result.SkippedRecordCount);
        Assert.IsFalse(block.Header.IsCapacityExhausted);
        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void WriteBatches_CopiesSequenceNumberIntoItsRecordSlot()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        var record = new MftRecord(20, 5, new MftRecordFields(1, sequenceNumber: 37), "record");

        MftBlockRowWriter.WriteBatches(new BlockWriter(block), [[record]],
            new MftScanOptions(), null, CancellationToken.None);

        Assert.AreEqual((ushort)37, block.SequenceNumbers[20]);
    }

    [TestMethod]
    public void WriteBatches_SlotCapacityOverflowIsCountedAndMarksCapacityExhaustion()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Record(32, "overflow"), Record(31, "last")]], new MftScanOptions(), null, CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount);
        Assert.AreEqual(32L, result.RowCount);
        Assert.AreEqual(4L, result.NamePoolUsedBytes);
        Assert.IsTrue(block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void WriteBatches_RecordNumberBeyondUInt32AndEmptyNamesAreSkipped()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Record((ulong)uint.MaxValue + 1, "overflow"), Record(6, ""), Record(7, "kept")]],
            new MftScanOptions(), null, CancellationToken.None);

        Assert.AreEqual(2L, result.SkippedRecordCount);
        Assert.AreEqual(8L, result.RowCount);
        Assert.AreEqual(4L, result.NamePoolUsedBytes);
        Assert.AreEqual(RowFlags.None, block.Rows[0].Flags);
        Assert.AreEqual(RowFlags.None, block.Rows[6].Flags);
        Assert.IsFalse(block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void WriteBatches_ParentRecordNumberBeyondUInt32IsSkipped()
    {
        // Both identifiers are 48-bit on disk. Truncating the parent instead of skipping
        // the record would silently point the child at an unrelated row rather than leave
        // it out of the block.
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[RecordWithParent(8, (ulong)uint.MaxValue + 1, "orphan"), Record(9, "kept")]],
            new MftScanOptions(), null, CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount);
        Assert.AreEqual(RowFlags.None, block.Rows[8].Flags);
        Assert.AreEqual("kept", NamePool.ReadRowName(block, 9).ToString());
    }

    [TestMethod]
    public void WriteBatches_NamePoolOverflowContinuesWithRecordsThatFit()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 4, volumeSerial: 123, rootRow: 5);
        var result = MftBlockRowWriter.WriteBatches(new BlockWriter(block),
            [[Record(5, "abc"), Record(6, "long"), Record(7, "z")]], new MftScanOptions(), null, CancellationToken.None);

        Assert.AreEqual(1L, result.SkippedRecordCount);
        Assert.AreEqual(8L, result.RowCount);
        Assert.AreEqual(4L, result.NamePoolUsedBytes);
        Assert.AreEqual(RowFlags.None, block.Rows[6].Flags);
        Assert.AreEqual("z", NamePool.ReadRowName(block, 7).ToString());
        Assert.IsTrue(block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void WriteBatches_WritesEachBatchBeforeRequestingTheNextAndReportsTransferCounts()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        var reports = new List<BlockWriteProgress>();
        var writer = new BlockWriter(block);

        IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            yield return [Record(5, ".")];
            Assert.AreEqual(".", NamePool.ReadRowName(writer.Block, 5).ToString());
            yield return [Record(20, "file"), Record(21, "")];
        }

        MftBlockRowWriter.WriteBatches(writer, Batches(), new MftScanOptions(),
            new SynchronousProgress<BlockWriteProgress>(reports.Add), CancellationToken.None);

        Assert.HasCount(2, reports);
        Assert.AreEqual(new BlockWriteProgress(1, 1, null, null), reports[0]);
        Assert.AreEqual(new BlockWriteProgress(2, 5, null, null), reports[1]);
    }

    [TestMethod]
    public void WriteBatches_CancelledBeforeStartingDoesNotEnumerate()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationToken = cancellation.Token;
        var writer = new BlockWriter(block);

        IEnumerable<IReadOnlyList<MftRecord>> Batches()
        {
            Assert.Fail("A cancelled scan must not request a batch.");
            yield break;
        }

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            MftBlockRowWriter.WriteBatches(writer, Batches(), new MftScanOptions(), null, cancellationToken));
        Assert.AreEqual(0u, block.Header.RowCount);
        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void WriteBatches_CancelledAfterBatchLeavesLaterRowsUnwritten()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        using var cancellation = new CancellationTokenSource();
        var writer = new BlockWriter(block);
        var progress = new CancellingProgress(cancellation);
        var cancellationToken = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() => MftBlockRowWriter.WriteBatches(
            writer, [[Record(5, ".")], [Record(6, "file")]], new MftScanOptions(), progress, cancellationToken));

        Assert.AreEqual(6u, block.Header.RowCount);
        Assert.AreEqual(RowFlags.None, block.Rows[6].Flags);
        Assert.IsFalse(block.Header.IsComplete);
    }

    [TestMethod]
    public void WriteBatches_EmptyInputReturnsExistingHeaderState()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(5, ".", new RowColumns(5, RowFlags.InUse, 0, 0, 0, 0)));
        writer.MarkCapacityExhausted();

        var result = MftBlockRowWriter.WriteBatches(writer, [], new MftScanOptions(), null, CancellationToken.None);

        Assert.AreEqual(new BlockWriteResult(6, 1, 0), result);
        Assert.IsTrue(block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public void WriteBatches_WritesEveryInUseRecordWhateverItsName()
    {
        using var block = TestBlocks.Create(_blockPath, ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 256, volumeSerial: 123, rootRow: 5);
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(3, FileAttributes.Directory), "directory"),
            new(10, 5, new MftRecordFields(1, FileAttributes.Normal, 100), "a.txt"),
            new(11, 5, new MftRecordFields(1, FileAttributes.Normal, 200), ".git"),
            new(12, 5, new MftRecordFields(1, FileAttributes.Normal, 300), "HEAD")
        ];

        var result = MftBlockRowWriter.WriteBatches(
            new BlockWriter(block), [records], new MftScanOptions(),
            null, CancellationToken.None);

        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, block.Rows[5].Flags);
        Assert.AreEqual(RowFlags.InUse, block.Rows[10].Flags);
        Assert.AreEqual(RowFlags.InUse, block.Rows[11].Flags);
        Assert.AreEqual(RowFlags.InUse, block.Rows[12].Flags);
        Assert.AreEqual(13L, result.RowCount);
        Assert.AreEqual(0L, result.SkippedRecordCount);
    }

    static MftRecord Record(ulong recordNumber, string name)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(1), name);
    }

    static MftRecord RecordWithParent(ulong recordNumber, ulong parentRecordNumber, string name)
    {
        return new MftRecord(recordNumber, parentRecordNumber, new MftRecordFields(1), name);
    }

    sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<BlockWriteProgress>
    {
        public void Report(BlockWriteProgress value)
        {
            cancellation.Cancel();
        }
    }

}
