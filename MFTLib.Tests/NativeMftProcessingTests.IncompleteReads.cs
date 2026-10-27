using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeMftProcessingTests
{
    const int MftRunOffset = 4096;
    const int BatchBytes = 256 * 1024; // 256 records of 1024 bytes
    const int AttributeListOffset = 0x80 * 4096;

    // An image whose $MFT is one 1 MiB run at cluster 1, which the parser reads in four 256-record batches.
    static byte[] BuildMultiBatchImage()
    {
        var data = BuildBootSector();
        WriteFileRecord(data, MftRunOffset);
        const int attribute = MftRunOffset + 0x38;
        WriteNonResidentDataAttribute(data, attribute, 1024L * 1024, 1, 256);
        WriteEndMarker(data, attribute + 0x48);
        return data;
    }

    // A one-cluster $MFT whose non-resident attribute list sits at AttributeListOffset.
    static byte[] BuildNonResidentAttributeListImage()
    {
        var data = BuildBootSector();
        WriteFileRecord(data, MftRunOffset);
        const int dataAttribute = MftRunOffset + 0x38;
        var dataLength = WriteNonResidentDataAttribute(data, dataAttribute, 4096, 1, 1);
        var listAttribute = dataAttribute + dataLength;
        data[listAttribute] = 0x20; // AttributeList
        data[listAttribute + 4] = 0x48;
        data[listAttribute + 8] = 0x01; // non-resident
        data[listAttribute + 0x20] = 0x40;
        var listSize = BitConverter.GetBytes(4096L);
        Array.Copy(listSize, 0, data, listAttribute + 0x28, 8);
        Array.Copy(listSize, 0, data, listAttribute + 0x30, 8);
        Array.Copy(listSize, 0, data, listAttribute + 0x38, 8);
        data[listAttribute + 0x40] = 0x21; // 1-byte length, 2-byte signed offset
        data[listAttribute + 0x41] = 0x01; // one cluster
        data[listAttribute + 0x42] = 0x80; // at cluster 128, which is AttributeListOffset
        data[listAttribute + 0x43] = 0x00;
        WriteEndMarker(data, listAttribute + 0x48);
        return data;
    }

    static void AssertParseFailedWithNoOutput(MftParseResult outcome)
    {
        Assert.IsFalse(string.IsNullOrEmpty(outcome.ErrorMessage), "an unreadable batch must fail the parse");
        Assert.AreEqual(0UL, outcome.UsedRecords);
        Assert.AreEqual(IntPtr.Zero, outcome.Entries);
    }

    [TestMethod]
    public void ParseMFTRecords_CompleteMultiBatchImage_ReportsNoError()
    {
        var image = BuildMultiBatchImage();
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        var outcome = NativeTestHooks.ParseImage(image);
        Assert.AreEqual(string.Empty, outcome.ErrorMessage);
        Assert.AreEqual(1024UL, outcome.TotalRecords);
    }

    [TestMethod]
    [DataRow(3, DisplayName = "first batch")]
    [DataRow(4, DisplayName = "second batch")]
    [DataRow(6, DisplayName = "last batch")]
    public void ParseMFTRecords_BatchReadFails_FailsAndDiscardsOutput(int failingRead)
    {
        var image = BuildMultiBatchImage();
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        NativeTestHooks.NativeSetReadFailCountdown(failingRead);
        AssertParseFailedWithNoOutput(NativeTestHooks.ParseImage(image));
    }

    [TestMethod]
    [DataRow(MftRunOffset + 1024, DisplayName = "first batch holds one record")]
    [DataRow(MftRunOffset + BatchBytes, DisplayName = "end of file at the second batch")]
    [DataRow(MftRunOffset + BatchBytes + 1024, DisplayName = "second batch holds one record")]
    [DataRow(MftRunOffset + (3 * BatchBytes) + 512, DisplayName = "last batch holds half a record")]
    public void ParseMFTRecords_ImageEndsInsideTheMft_FailsAndDiscardsOutput(int length)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        AssertParseFailedWithNoOutput(NativeTestHooks.ParseImage(BuildMultiBatchImage()[..length]));
    }

    [TestMethod]
    [DataRow(AttributeListOffset, DisplayName = "end of file at the run")]
    [DataRow(AttributeListOffset + 512, DisplayName = "end of file inside the run")]
    public void ParseMFTRecords_NonResidentAttributeListRunEndsEarly_FailsTheParse(int length)
    {
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        var outcome = NativeTestHooks.ParseImage(BuildNonResidentAttributeListImage()[..length]);
        AssertParseFailedWithNoOutput(outcome);
        Assert.AreEqual("The volume could not be read completely.", outcome.ErrorMessage);
    }

    [TestMethod]
    [DataRow(5120, DisplayName = "extension starts at end of file")]
    [DataRow(5632, DisplayName = "extension is half a record")]
    public void ParseMFTRecords_ExtensionRecordReadEndsEarly_FailsTheParse(int length)
    {
        var image = BuildMultiBatchImage();
        const int list = MftRunOffset + 0x38 + 0x48;
        Array.Clear(image, list, sizeof(uint)); // replace the existing end marker's complete type code
        WriteEndMarker(image, list + WriteResidentAttributeList(image, list, 1));
        WriteFileRecord(image, 5120, 2);
        WriteEndMarker(image, 5120 + 0x38);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        var outcome = NativeTestHooks.ParseImage(image[..length]);
        AssertParseFailedWithNoOutput(outcome);
        Assert.AreEqual("The volume could not be read completely.", outcome.ErrorMessage);
        Assert.AreEqual(0UL, outcome.TotalRecords, "reject the extension before starting the batch scan");
    }
}
