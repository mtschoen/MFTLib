using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

// Volume reads under SetVolumeAlignmentOverride, which fails every read whose destination, offset or length is not a
// multiple of the armed alignment, as a noncached volume handle may. The images have 1024-byte records and 4096-byte
// clusters, so 512 is an alignment the records keep and 4096 one they do not.
public partial class NativeMftProcessingTests
{
    static (ulong Number, ulong Parent, string Name, long Size, ushort Sequence)[] ScanImage(string path)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        using var image = File.OpenHandle(path);
        using var result =
            new MftResult(MFTLibNative.Hooks._parseMftRecordsWithProgress(image, false, 64, IntPtr.Zero, default));
        return result.ReadBatches(4096).SelectMany(batch => batch)
            .Select(row => (row.RecordNumber, row.ParentRecordNumber, row.FileName, row.Size, row.SequenceNumber))
            .ToArray();
    }

    static MftParseResult ParseImageUnderAlignment(byte[] image, uint alignment)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        NativeTestHooks.NativeSetVolumeAlignmentOverride(alignment, alignment);
        return NativeTestHooks.ParseImage(image);
    }

    // Each chunk read lands in the chunk buffer directly at both alignments. At 4096 only the 512-byte boot sector and
    // the 1024-byte record 0 go through the bounce buffer.
    [TestMethod]
    [DataRow(512U, 0UL)]
    [DataRow(4096U, 2UL)]
    public void Scan_UnderAnEnforcedAlignment_ReturnsTheRecordsOfAnUnenforcedScan(uint alignment, ulong bouncedReads)
    {
        using var file = new TempFile(create: false);
        SyntheticNtfsImage.Write(file.Path, 512);
        var unenforced = ScanImage(file.Path);
        NativeTestHooks.NativeResetTestState();
        NativeTestHooks.NativeSetVolumeAlignmentOverride(alignment, alignment);

        var enforced = ScanImage(file.Path);

        Assert.IsGreaterThan(300, unenforced.Length);
        CollectionAssert.AreEqual(unenforced, enforced);
        Assert.AreEqual(bouncedReads, NativeTestHooks.NativeGetBouncedVolumeReads());
    }

    // Record 0's attribute list names extension records 1 and 2, each read on its own: directly at 512, through the
    // bounce buffer at 4096.
    [TestMethod]
    [DataRow(512U, 0UL)]
    [DataRow(4096U, 4UL)]
    public void LayoutWithExtensionRecords_UnderAnEnforcedAlignment_ReadsEachExtension(uint alignment,
        ulong bouncedReads)
    {
        var data = BuildBootSector();
        WriteFileRecord(data, 4096);
        const int dataAttribute = 4096 + 0x38;
        var dataLength = WriteNonResidentDataAttribute(data, dataAttribute, 1024L * 1024, 1, 256);
        var attributeList = dataAttribute + dataLength;
        var attributeListLength = WriteResidentAttributeList(data, attributeList, 1, 2);
        WriteEndMarker(data, attributeList + attributeListLength);
        WriteFileRecord(data, 5120, 2);
        WriteEndMarker(data, 5120 + 0x38);
        WriteFileRecord(data, 6144, 3);
        WriteEndMarker(data, 6144 + 0x38);

        var result = ParseImageUnderAlignment(data, alignment);

        Assert.AreEqual(string.Empty, result.ErrorMessage);
        Assert.AreEqual(1024UL, result.TotalRecords);
        Assert.AreEqual(bouncedReads, NativeTestHooks.NativeGetBouncedVolumeReads());
    }

    // A nonresident attribute list is read in whole clusters into an aligned buffer, so the list read needs no bounce
    // at either alignment; the extension record it names is read on its own, and at 4096 that read and the boot sector
    // and record 0 go through the bounce buffer. The extension adds 32 clusters to the $MFT's 64, so the exact total
    // shows the list was read.
    [TestMethod]
    [DataRow(512U, 0UL)]
    [DataRow(4096U, 3UL)]
    public void LayoutWithANonresidentAttributeList_UnderAnEnforcedAlignment_ReadsTheListDirectly(uint alignment,
        ulong bouncedReads)
    {
        var data = BuildBootSector();
        WriteFileRecord(data, 4096);
        const int dataAttribute = 4096 + 0x38;
        var dataLength = WriteNonResidentDataAttribute(data, dataAttribute, 64L * 4096, 1, 64);
        var attributeList = dataAttribute + dataLength;
        data[attributeList] = 0x20;
        data[attributeList + 4] = 0x48;
        data[attributeList + 8] = 0x01;
        data[attributeList + 0x20] = 0x40;
        foreach (var field in new[] { 0x28, 0x30, 0x38 })
        {
            BitConverter.GetBytes(512L).CopyTo(data, attributeList + field);
        }

        // One cluster at cluster 128 holding one entry: an unnamed $DATA in record 1.
        data[attributeList + 0x40] = 0x21;
        data[attributeList + 0x41] = 0x01;
        data[attributeList + 0x42] = 0x80;
        WriteEndMarker(data, attributeList + 0x48);
        const int entry = 128 * 4096;
        data[entry] = 0x80;
        data[entry + 4] = 28;
        data[entry + 16] = 1;

        WriteFileRecord(data, 5120, 2);
        WriteNonResidentDataAttribute(data, 5120 + 0x38, 32L * 4096, 65, 32);
        WriteEndMarker(data, 5120 + 0x38 + 0x48);

        var result = ParseImageUnderAlignment(data, alignment);

        Assert.AreEqual(string.Empty, result.ErrorMessage);
        Assert.AreEqual(384UL, result.TotalRecords);
        Assert.AreEqual(bouncedReads, NativeTestHooks.NativeGetBouncedVolumeReads());
    }
}
