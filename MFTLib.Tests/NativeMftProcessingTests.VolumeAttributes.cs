using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeMftProcessingTests
{
    // --- FindAttribute null return ---

    [TestMethod]
    public void ParseMFTRecords_WithNtfsHeaderButBadMagic_ReturnsError()
    {
        // Create a file that looks like NTFS boot sector but has invalid MFT record 0
        var data = BuildBootSector();

        var result = NativeTestHooks.ParseImage(data);
        var errorMessage = result.ErrorMessage;
        Assert.IsTrue(
            errorMessage!.Contains("magic") || errorMessage.Contains("MFT record 0"),
            $"Unexpected error: {errorMessage}");
    }

    [TestMethod]
    public void ParseMFTRecords_WithValidMagicButNoDataAttribute_ReturnsError()
    {
        // Create a file with valid NTFS BPB + valid FILE magic in record 0,
        // but no Data attribute  -  triggers FindAttribute returning nullptr
        var data = BuildBootSector();
        const int recordOffset = 4096;
        WriteFileRecord(data, recordOffset);
        WriteEndMarker(data, recordOffset + 0x38);

        var result = NativeTestHooks.ParseImage(data);
        var errorMessage = result.ErrorMessage;
        Assert.IsTrue(
            errorMessage!.Contains("Data attribute") || errorMessage.Contains("magic"),
            $"Unexpected error: {errorMessage}");
    }

    [TestMethod]
    public void ParseMFTRecords_ZeroLengthAttribute_ReturnsMalformedAttributeError()
    {
        var data = BuildBootSector();
        WriteFileRecord(data, 4096);
        data[4096 + 0x38] = 0x10;

        var result = NativeTestHooks.ParseImage(data);
        Assert.Contains("malformed attribute", result.ErrorMessage);
    }

    // Synthetic image handles exercise batch, attribute-list and extension-record read failures
    // without administrator access. Each failure rejects the scan with no entries.

    // --- ParseMFTRecords Read failure on MFT record 0 ---

    [TestMethod]
    public void ParseMFTRecords_ReadFailOnMftRecord0_ReturnsError()
    {
        // Create a file with valid NTFS BPB so boot sector read succeeds,
        // then fail the 2nd Read (MFT record 0)
        var data = new byte[1024 * 1024];
        data[3] = (byte)'N';
        data[4] = (byte)'T';
        data[5] = (byte)'F';
        data[6] = (byte)'S';
        data[0x0B] = 0x00;
        data[0x0C] = 0x02;
        data[0x0D] = 0x08;
        data[0x30] = 0x01;

        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        NativeTestHooks.NativeSetReadFailCountdown(2); // fail 2nd Read
        var result = NativeTestHooks.ParseImage(data);
        Assert.Contains("MFT record 0", result.ErrorMessage);
    }

    // --- Resident AttributeList path ---

    [TestMethod]
    public void ParseMFTRecords_ResidentAttributeList_EmptyValueReturnsDataRecords()
    {
        // Record 0 with Data + empty resident AttributeList.
        var data = BuildBootSector();
        WriteFileRecord(data, 4096);

        const int a1 = 4096 + 0x38;
        var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

        // Resident AttributeList with empty value (no extension records)
        var a2 = a1 + a1Len;
        data[a2] = 0x20; // TypeCode = AttributeList
        data[a2 + 4] = 0x20; // RecordLength = 32
        data[a2 + 8] = 0x00; // FormCode = resident
        data[a2 + 0x10] = 0x00; // ValueLength = 0
        data[a2 + 0x14] = 0x18; // ValueOffset

        WriteEndMarker(data, a2 + 0x20);

        var result = NativeTestHooks.ParseImage(data);
        Assert.IsGreaterThan(0UL, result.TotalRecords);
    }
}
