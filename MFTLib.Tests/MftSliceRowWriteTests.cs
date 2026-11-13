using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Records packed into the native slice layout read back unchanged, and the extension data sizes a streamed parse
///     delivers after its last slice reach the written rows under the native whole-result rule.
/// </summary>
[TestClass]
public sealed unsafe class MftSliceRowWriteTests : IDisposable
{
    const ushort InUse = 1;
    const ushort Directory = 2;
    const ushort SizeUnknown = 0x8000;

    readonly TempDirectory _tempDirectory = new("mftlib-slice-rows");

    public void Dispose() => _tempDirectory.Dispose();

    [TestMethod]
    public void Pack_EveryField_ReadsBackFromThePackedEntry()
    {
        MftRecord[] records =
        [
            new(5, 5, new MftRecordFields(InUse | Directory, FileAttributes.Directory, 0, 1234, 5, 5), "."),
            new(0x1_0000_0007, 0x2_0000_0005, new MftRecordFields(SizeUnknown, FileAttributes.Hidden, -3,
                long.MaxValue, ushort.MaxValue, 9), "freed name")
        ];

        var slice = MftParsedSlice.Pack(records);
        try
        {
            Assert.AreEqual(2, slice.EntryCount);
            Assert.AreEqual(11, slice.StringUnits);
            fixed (byte* table = slice.Entries)
            {
                for (var index = 0; index < records.Length; index++)
                {
                    var record = records[index];
                    var entry = new MftPackedEntry(table + (index * (int)MFTLibNative.NativeCompactEntrySize));
                    Assert.AreEqual(record.RecordNumber, entry.RecordNumber);
                    Assert.AreEqual(record.ParentRecordNumber, entry.ParentRecordNumber);
                    Assert.AreEqual(record.Flags, entry.Flags);
                    Assert.AreEqual((uint)record.FileAttributes, entry.FileAttributes);
                    Assert.AreEqual(record.Size, entry.Size);
                    Assert.AreEqual(record.ModifiedFileTime, entry.ModifiedFileTime);
                    Assert.AreEqual(record.SequenceNumber, entry.SequenceNumber);
                    Assert.AreEqual(record.ParentSequenceNumber, entry.ParentSequenceNumber);
                    Assert.AreEqual(record.FileName,
                        new string(slice.Strings, (int)entry.StringOffset, entry.StringLength));
                }
            }
        }
        finally
        {
            slice.Return();
        }
    }

    [TestMethod]
    public void Pack_ANameLongerThanAPackedLength_IsRefused()
    {
        var record = new MftRecord(7, 5, new MftRecordFields(InUse), new string('a', ushort.MaxValue + 1));

        Assert.ThrowsExactly<ArgumentException>(() => MftParsedSlice.Pack([record]));
    }

    [TestMethod]
    public void Write_ExtensionDataSizes_ApplyOnlyToWrittenUnknownSizeFilesOfTheSameSequence()
    {
        using var block = TestBlocks.Create(Path.Combine(_tempDirectory.Path, "sizes.mlix"), ProducerKind.Mft,
            slotCapacity: 32, namePoolCapacity: 1024, volumeSerial: 0, rootRow: 5, deleteOnClose: true);
        MftRecord[] records =
        [
            Record(5, InUse | Directory, sequence: 5, name: "."),
            Record(10, InUse | SizeUnknown, sequence: 3),
            Record(11, InUse | SizeUnknown, sequence: 3),
            Record(12, InUse | Directory | SizeUnknown, sequence: 3),
            Record(13, InUse, sequence: 3, size: 77),
            Record(14, SizeUnknown, sequence: 3)
        ];
        MftExtensionDataSize[] sizes =
        [
            new(10, 1000, 3), new(11, 1100, 4), new(12, 1200, 3), new(13, 1300, 3), new(14, 1400, 3),
            new(20, 2000, 3), new(0x1_0000_0000, 3000, 3)
        ];

        using var source = new CompletingSource(records, sizes);
        var result = MftBlockRowWriter.Write(new BlockWriter(block), source,
            new MftScanOptions { IncludeFreed = true }, null, CancellationToken.None);

        Assert.AreEqual(0L, result.SkippedRecordCount);
        AssertSize(block, 10, 1000, known: true);
        AssertSize(block, 11, 0, known: false);
        Assert.IsTrue(block.Rows[12].IsDirectory);
        AssertSize(block, 12, 0, known: false);
        AssertSize(block, 13, 77, known: true);
        Assert.IsTrue(block.Rows[14].IsDeleted, "the freed record is a deleted row");
        AssertSize(block, 14, 1400, known: true);
        Assert.IsFalse(block.Rows[20].IsInUse, "a size for a row never written writes nothing");
    }

    static MftRecord Record(ulong number, ushort flags, ushort sequence, string? name = null, long size = 0) =>
        new(number, 5, new MftRecordFields(flags, 0, size, 0, sequence, 5), name ?? $"file{number}");

    static void AssertSize(BlockFile block, int row, long size, bool known)
    {
        Assert.AreEqual(size, block.Rows[row].Size, $"row {row} size");
        Assert.AreEqual(!known, (block.Rows[row].Flags & RowFlags.SizeUnknown) != 0, $"row {row} size known");
    }

    // Records packed as one slice, completed with the extension data sizes a streamed parse delivers last.
    sealed class CompletingSource(IReadOnlyList<MftRecord> records, MftExtensionDataSize[] sizes) : IMftSliceSource
    {
        public MftRowValidation Validation => MftRowValidation.None;

        public IEnumerable<MftParsedSlice> Slices() => [MftParsedSlice.Pack(records)];

        public MftSliceCompletion Finish() => new(0, sizes);

        public void Dispose()
        {
        }
    }
}
