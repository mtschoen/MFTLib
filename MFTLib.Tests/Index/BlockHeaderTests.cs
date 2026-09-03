using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockHeaderTests
{
    static BlockHeader ValidHeader()
    {
        return new BlockHeader
        {
            Magic = BlockLayout.Magic,
            FormatVersion = BlockLayout.FormatVersion,
            ProducerKind = ProducerKind.Enumeration,
            Flags = BlockFlags.Complete,
            VolumeSerial = 0xDEADBEEF,
            RootRow = 0,
            ScanTimestampTicks = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc).Ticks,
            RowCount = 10,
            SlotCapacity = 100,
            NamePoolUsed = 40,
            NamePoolCapacity = 1000,
            UsnJournalId = 0,
            UsnNextUsn = 0,
            Generation = 1,
            RowRegionOffset = BlockLayout.RowRegionOffset,
            NamePoolOffset = (ulong)BlockLayout.NamePoolOffset(100)
        };
    }

    static long ValidFileLength()
    {
        return BlockLayout.TotalBlockBytes(100, 1000);
    }

    [TestMethod]
    public void Header_IsExactlyEightyEightBytes()
    {
        Assert.AreEqual(BlockLayout.HeaderFieldBytes, Marshal.SizeOf<BlockHeader>());
    }

    [TestMethod]
    public void Header_EightByteFieldsAreEightByteAligned()
    {
        Assert.AreEqual(24, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.ScanTimestampTicks)));
        Assert.AreEqual(48, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.UsnJournalId)));
        Assert.AreEqual(56, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.UsnNextUsn)));
        Assert.AreEqual(64, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.Generation)));
        Assert.AreEqual(72, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.RowRegionOffset)));
        Assert.AreEqual(80, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.NamePoolOffset)));
    }

    [TestMethod]
    public void Header_FieldsFollowSpecOrder()
    {
        Assert.AreEqual(0, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.Magic)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.FormatVersion)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.ProducerKind)));
        Assert.AreEqual(12, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.Flags)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.VolumeSerial)));
        Assert.AreEqual(20, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.RootRow)));
        Assert.AreEqual(32, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.RowCount)));
        Assert.AreEqual(36, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.SlotCapacity)));
        Assert.AreEqual(40, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.NamePoolUsed)));
        Assert.AreEqual(44, (int)Marshal.OffsetOf<BlockHeader>(nameof(BlockHeader.NamePoolCapacity)));
    }

    [TestMethod]
    public void Validate_GoodHeader_ReturnsValid()
    {
        var header = ValidHeader();
        Assert.AreEqual(BlockValidationResult.Valid,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_WrongMagic_IsRejected()
    {
        var header = ValidHeader();
        header.Magic = 0x11111111;
        Assert.AreEqual(BlockValidationResult.WrongMagic,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_WrongFormatVersion_IsRejected()
    {
        var header = ValidHeader();
        header.FormatVersion = BlockLayout.FormatVersion + 1;
        Assert.AreEqual(BlockValidationResult.WrongFormatVersion,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_MissingCompleteFlag_IsRejected()
    {
        var header = ValidHeader();
        header.Flags = BlockFlags.None;
        Assert.AreEqual(BlockValidationResult.Incomplete,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_WrongVolumeSerial_IsRejected()
    {
        var header = ValidHeader();
        Assert.AreEqual(BlockValidationResult.WrongVolumeSerial,
            BlockHeader.Validate(in header, 0x00000001, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_RowCountPastCapacity_IsInconsistent()
    {
        var header = ValidHeader();
        header.RowCount = header.SlotCapacity + 1;
        Assert.AreEqual(BlockValidationResult.InconsistentRegions,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_RootRowOutsidePublishedRows_IsInconsistent()
    {
        var header = ValidHeader();
        header.RootRow = header.RowCount;
        Assert.AreEqual(BlockValidationResult.InconsistentRegions,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void Validate_FileShorterThanRegions_IsInconsistent()
    {
        var header = ValidHeader();
        Assert.AreEqual(BlockValidationResult.InconsistentRegions,
            BlockHeader.Validate(in header, 0xDEADBEEF, BlockLayout.PageSize));
    }

    [TestMethod]
    public void Validate_NamePoolUsedPastCapacity_IsInconsistent()
    {
        var header = ValidHeader();
        header.NamePoolUsed = header.NamePoolCapacity + 2;
        Assert.AreEqual(BlockValidationResult.InconsistentRegions,
            BlockHeader.Validate(in header, 0xDEADBEEF, ValidFileLength()));
    }

    [TestMethod]
    public void CompletionAndCompactionProperties_ReadFlags()
    {
        var header = ValidHeader();
        Assert.IsTrue(header.IsComplete);
        Assert.IsFalse(header.IsCompactionNeeded);
        header.Flags |= BlockFlags.CompactionNeeded;
        Assert.IsTrue(header.IsCompactionNeeded);
    }
}
