using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileRowTests
{
    [TestMethod]
    public void Row_IsExactlyThirtyTwoBytes()
    {
        Assert.AreEqual(BlockLayout.RowBytes, Marshal.SizeOf<FileRow>());
    }

    [TestMethod]
    public void Row_FieldsFollowSpecOrderAndOffsets()
    {
        Assert.AreEqual(0, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.ParentRow)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.Attributes)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.NameOffsetBytes)));
        Assert.AreEqual(12, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.NameLengthUnits)));
        Assert.AreEqual(14, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.Flags)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.Size)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.ModifiedTicks)));
    }

    [TestMethod]
    public void DescriptorWord_StartsAtOffsetEightSoItIsEightByteAligned()
    {
        // Rows are 32 bytes and the row region starts at a 4 KB boundary, so byte 8 of every
        // row in a mapped block lands on an 8-byte boundary. That is what makes the single
        // 64-bit store in a rename atomic rather than merely quick.
        Assert.AreEqual(8, (int)Marshal.OffsetOf<FileRow>(nameof(FileRow.NameOffsetBytes)));
        Assert.AreEqual(0, BlockLayout.RowBytes % sizeof(ulong));
        Assert.AreEqual(0L, BlockLayout.RowRegionOffset % sizeof(ulong));
    }

    [TestMethod]
    public void DescriptorWord_CarriesNameOffsetLengthAndFlagsTogether()
    {
        var row = default(FileRow);
        FileRow.WriteDescriptorWord(ref row, 0x11223344, 0x5566,
            RowFlags.InUse | RowFlags.Tombstone);

        Assert.AreEqual(0x11223344u, row.NameOffsetBytes);
        Assert.AreEqual((ushort)0x5566, row.NameLengthUnits);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, row.Flags);

        var descriptor = FileRow.ReadDescriptorWord(in row);
        Assert.AreEqual(0x11223344u, FileRow.DescriptorNameOffsetBytes(descriptor));
        Assert.AreEqual((ushort)0x5566, FileRow.DescriptorNameLengthUnits(descriptor));
        Assert.AreEqual(RowFlags.InUse | RowFlags.Tombstone, FileRow.DescriptorFlags(descriptor));
    }

    [TestMethod]
    public void DescriptorWord_LeavesTheOtherColumnsUntouched()
    {
        var row = new FileRow
        {
            ParentRow = 7,
            Attributes = 0x20,
            Size = 4096,
            ModifiedTicks = 123456789
        };

        FileRow.WriteDescriptorWord(ref row, 64, 8, RowFlags.InUse);

        Assert.AreEqual(7u, row.ParentRow);
        Assert.AreEqual(0x20u, row.Attributes);
        Assert.AreEqual(4096L, row.Size);
        Assert.AreEqual(123456789L, row.ModifiedTicks);
    }

    [TestMethod]
    public void DefaultRow_IsNotInUse()
    {
        var row = default(FileRow);
        Assert.IsFalse(row.IsInUse);
        Assert.IsFalse(row.IsDirectory);
        Assert.IsFalse(row.IsDeleted);
    }

    [TestMethod]
    public void SizeKnown_IsTrueUnlessSizeUnknownFlagSet()
    {
        var row = new FileRow { Flags = RowFlags.InUse };
        Assert.IsTrue(row.SizeKnown);
        row.Flags |= RowFlags.SizeUnknown;
        Assert.IsFalse(row.SizeKnown);
    }

    [TestMethod]
    public void ModifiedUtc_ReadsTicksAsUtc()
    {
        var moment = new DateTime(2026, 9, 2, 12, 30, 0, DateTimeKind.Utc);
        var row = new FileRow { ModifiedTicks = moment.Ticks };
        Assert.AreEqual(moment, row.ModifiedUtc);
        Assert.AreEqual(DateTimeKind.Utc, row.ModifiedUtc.Kind);
    }

    [TestMethod]
    public void TombstoneRow_ReadsAsDeletedButKeepsName()
    {
        var row = new FileRow
        {
            Flags = RowFlags.InUse | RowFlags.Tombstone,
            NameOffsetBytes = 64,
            NameLengthUnits = 8
        };
        Assert.IsTrue(row.IsDeleted);
        Assert.AreEqual(64u, row.NameOffsetBytes);
        Assert.AreEqual((ushort)8, row.NameLengthUnits);
    }
}
