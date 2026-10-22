using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

// Images whose sizes, run lengths and attribute chains are forged. Each must be rejected or ignored without
// reading outside the buffers the parser owns.
public partial class NativeMftProcessingTests
{
    private const int BaseMftRecords = 1024;
    private const int ExtensionRecordOffset = MftRunOffset + 2 * 1024; // segment 2
    private const int ExtensionAddedRecords = 4; // one 4096-byte cluster of 1024-byte records
    private const int AttributeListSegmentTwo = 2;
    private const ulong RunLengthThatWrapsTheByteCount = 1UL << 52; // times 4096 bytes per cluster is 2^64

    private readonly record struct ForgedRun(ulong ClusterCount, long ClusterDelta, int CountBytes, int DeltaBytes);

    private static byte[] EncodeRuns(params ForgedRun[] runs)
    {
        var bytes = new List<byte>();
        foreach (var run in runs)
        {
            bytes.Add((byte)((run.DeltaBytes << 4) | run.CountBytes));
            for (var index = 0; index < run.CountBytes; index++)
            {
                bytes.Add(index < sizeof(ulong) ? (byte)(run.ClusterCount >> (index * 8)) : (byte)0);
            }

            for (var index = 0; index < run.DeltaBytes; index++)
            {
                bytes.Add(index < sizeof(long) ? (byte)((ulong)run.ClusterDelta >> (index * 8)) : (byte)0);
            }
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private static int WriteNonResidentAttribute(byte[] data, int offset, byte typeCode, long fileSize,
        byte[] mappingPairs)
    {
        var length = (0x40 + mappingPairs.Length + 7) & ~7;
        data[offset] = typeCode;
        BitConverter.GetBytes(length).CopyTo(data, offset + 4);
        data[offset + 8] = 0x01;
        data[offset + 0x20] = 0x40;
        var sizeBytes = BitConverter.GetBytes(fileSize);
        Array.Copy(sizeBytes, 0, data, offset + 0x28, 8);
        Array.Copy(sizeBytes, 0, data, offset + 0x30, 8);
        Array.Copy(sizeBytes, 0, data, offset + 0x38, 8);
        mappingPairs.CopyTo(data, offset + 0x40);
        return length;
    }

    // Record 0 holding a 1 MiB $MFT data attribute at cluster 1; nextAttribute is where the caller continues.
    private static byte[] BuildRecordZeroImage(out int nextAttribute, byte[]? mappingPairs = null)
    {
        var data = BuildBootSector();
        WriteFileRecord(data, MftRunOffset);
        var first = MftRunOffset + 0x38;
        var length = WriteNonResidentAttribute(data, first, 0x80, 1024L * 1024,
            mappingPairs ?? EncodeRuns(new ForgedRun(256, 1, 2, 1)));
        nextAttribute = first + length;
        return data;
    }

    // An attribute-list entry at listOffset naming segment two, and segment two itself: an extension record
    // whose data attribute adds one cluster, four records, to the $MFT.
    private static void WriteExtensionSegmentTwo(byte[] data, int listOffset)
    {
        data[listOffset] = 0x80;
        data[listOffset + 4] = 28;
        BitConverter.GetBytes((uint)AttributeListSegmentTwo).CopyTo(data, listOffset + 16);
        WriteFileRecord(data, ExtensionRecordOffset, 3);
        var attribute = ExtensionRecordOffset + 0x38;
        WriteEndMarker(data, attribute + WriteNonResidentDataAttribute(data, attribute, 4096, 300, 1));
    }

    private static void AssertParsedAs(byte[] image, ulong expectedRecords, string because)
    {
        var outcome = ParseImage(image, image.Length);
        Assert.AreEqual(string.Empty, outcome.ErrorMessage, because);
        Assert.AreEqual(expectedRecords, outcome.TotalRecords, because);
    }

    private static void AssertRejected(byte[] image, string because, int failingRead = 0)
    {
        var outcome = ParseImage(image, image.Length, failingRead);
        Assert.IsFalse(string.IsNullOrEmpty(outcome.ErrorMessage), because);
        Assert.AreEqual(0UL, outcome.UsedRecords, because);
        Assert.AreEqual(IntPtr.Zero, outcome.Entries, because);
    }

    // The non-resident attribute list at AttributeListOffset, with the given runs and size.
    private static byte[] BuildImageWithNonResidentList(long listFileSize, params ForgedRun[] listRuns)
    {
        var data = BuildRecordZeroImage(out var next);
        var length = WriteNonResidentAttribute(data, next, 0x20, listFileSize, EncodeRuns(listRuns));
        WriteEndMarker(data, next + length);
        WriteExtensionSegmentTwo(data, AttributeListOffset);
        return data;
    }

    [TestMethod]
    public void ParseMFTRecords_ExtensionListWithoutForgedRuns_AddsTheExtensionRecords()
    {
        var image = BuildImageWithNonResidentList(4096, new ForgedRun(1, 128, 1, 2));
        AssertParsedAs(image, BaseMftRecords + ExtensionAddedRecords, "the control image merges its extension");
    }

    [TestMethod]
    public void ParseMFTRecords_AttributeListRunLengthsWrapTheByteTotal_FailsTheParse()
    {
        var image = BuildImageWithNonResidentList(
            4096, new ForgedRun(1, 128, 1, 2), new ForgedRun(RunLengthThatWrapsTheByteCount, 0, 7, 1));
        AssertIncompleteExtensionInput(image);
    }

    [TestMethod]
    public void ParseMFTRecords_AttributeListSizeExceedsItsRuns_FailsTheParse()
    {
        var image = BuildImageWithNonResidentList(8192, new ForgedRun(1, 128, 1, 2));
        AssertIncompleteExtensionInput(image);
    }

    [TestMethod]
    [DataRow((1L << 51) + 1, DisplayName = "starts beyond the range")]
    [DataRow((1L << 51) - 1, DisplayName = "ends beyond the range")]
    public void ParseMFTRecords_AttributeListRunBeyondTheSeekRange_FailsTheParse(long cluster)
    {
        // Cluster 2^51 + 1 is byte 2^63 + 4096. A signed seek there fails, and a read that ignores the failure
        // takes its bytes from the previous read's position, where this image keeps a valid list entry.
        var data = BuildRecordZeroImage(out var next);
        var length = WriteNonResidentAttribute(
            data, next, 0x20, 4096, EncodeRuns(new ForgedRun(1, cluster, 1, 7)));
        WriteEndMarker(data, next + length);
        WriteExtensionSegmentTwo(data, MftRunOffset + 1024);
        AssertIncompleteExtensionInput(data);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "no extension lookup")]
    [DataRow(true, DisplayName = "extension lookup walks the runs")]
    public void ParseMFTRecords_MftRunLengthsWrapTheByteTotal_FailsTheParse(bool lookUpAnExtension)
    {
        var data = BuildRecordZeroImage(out var next,
            EncodeRuns(new ForgedRun(RunLengthThatWrapsTheByteCount, 1, 7, 1), new ForgedRun(256, 0, 2, 1)));
        if (lookUpAnExtension)
        {
            next += WriteResidentAttributeList(data, next, AttributeListSegmentTwo);
        }

        WriteEndMarker(data, next);
        AssertRejected(data, "the runs do not describe a $MFT of a size that fits in 64 bits");
    }

    [TestMethod]
    [DataRow(1, DisplayName = "one run whose extent passes 64 bits")]
    [DataRow(4, DisplayName = "four runs whose lengths sum to 64 bits")]
    public void ParseMFTRecords_MftRunsWhoseSumsOverflow_FailsTheParse(int shape)
    {
        var runs = shape == 1
            ? new[] { new ForgedRun(RunLengthThatWrapsTheByteCount - 1, 1, 7, 1) }
            : Enumerable.Repeat(new ForgedRun(1UL << 50, 0, 7, 1), 4).ToArray();
        var data = BuildRecordZeroImage(out var next, EncodeRuns(runs));
        WriteEndMarker(data, next);
        AssertRejected(data, "the run extents and lengths must fit 64 bits");
    }

    [TestMethod]
    public void ParseMFTRecords_AttributeListShorterThanItsHeader_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next);
        data[next] = 0x20;
        data[next + 4] = 0x30;
        data[next + 8] = 0x01; // non-resident, but too short to hold the non-resident header
        WriteEndMarker(data, next + 0x30);
        AssertIncompleteExtensionInput(data);
    }

    [TestMethod]
    public void ParseMFTRecords_DataAttributeMappingPairsPastItsEnd_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next);
        data[MftRunOffset + 0x38 + 0x20] = 0x50; // the attribute is 0x48 long
        WriteEndMarker(data, next);
        AssertRejected(data, "the mapping pairs start outside the attribute");
    }

    [TestMethod]
    public void ParseMFTRecords_ExtensionRecordWithoutFileSignature_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next);
        next += WriteResidentAttributeList(data, next, AttributeListSegmentTwo);
        WriteEndMarker(data, next);
        WriteExtensionSegmentTwo(data, MftRunOffset + 1024);
        data[ExtensionRecordOffset] = 0x00; // the update sequence array is intact, the signature is not
        AssertInvalidExtensionInput(data, "Invalid MFT extension record magic");
    }

    [TestMethod]
    public void ParseMFTRecords_MftStartWrapsTheByteOffset_FailsTheParse()
    {
        var image = BuildMultiBatchImage();
        BitConverter.GetBytes((1UL << 52) + 1).CopyTo(image, 0x30); // times 4096 bytes per cluster wraps to 4096
        AssertRejected(image, "the wrapped offset reads record 0 from where it happens to be");
    }

    [TestMethod]
    public void ParseMFTRecords_MftRunBeforeTheStartOfTheVolume_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next, EncodeRuns(new ForgedRun(256, -1, 2, 1)));
        WriteEndMarker(data, next);
        AssertRejected(data, "a negative cluster is outside the volume");
    }

    [TestMethod]
    public void ParseMFTRecords_LengthFieldWiderThanEightBytes_FailsTheParse()
    {
        byte[] pairs = [0x19, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0];
        var data = BuildRecordZeroImage(out var next, pairs);
        WriteEndMarker(data, next);
        AssertRejected(data, "a field above eight bytes does not fit the 64-bit value it is shifted into");
    }

    [TestMethod]
    public void ParseMFTRecords_OffsetFieldWiderThanEightBytes_FailsTheParse()
    {
        byte[] pairs = [0x91, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var data = BuildRecordZeroImage(out var next, pairs);
        WriteEndMarker(data, next);
        AssertRejected(data, "an offset field above eight bytes does not fit the 64-bit value it is shifted into");
    }

    [TestMethod]
    public void ParseMFTRecords_RunFieldCutOffByTheAttributeEnd_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next);
        var attribute = MftRunOffset + 0x38;
        data[attribute + 0x20] = 0x44; // the run header sits four bytes before the attribute's end
        data[attribute + 0x44] = 0x18; // an eight-byte length with only three bytes left
        data[attribute + 0x45] = 0x01;
        WriteEndMarker(data, next);
        AssertRejected(data, "a run cut off by the end of its attribute is not a run");
    }

    [TestMethod]
    [DataRow(0, DisplayName = "attribute longer than the record")]
    [DataRow(1, DisplayName = "attribute chain without an end marker")]
    [DataRow(2, DisplayName = "first attribute outside the record")]
    public void ParseMFTRecords_RecordZeroAttributeChainLeavesTheRecord_FailsTheParse(int kind)
    {
        var data = BuildRecordZeroImage(out var next);
        switch (kind)
        {
            case 0:
                data[next] = 0x30;
                BitConverter.GetBytes(0x2000).CopyTo(data, next + 4);
                break;
            case 1:
                for (var attribute = next; attribute + 0x20 <= MftRunOffset + 1024; attribute += 0x20)
                {
                    data[attribute] = 0x30;
                    data[attribute + 4] = 0x20;
                }

                break;
            default:
                BitConverter.GetBytes((ushort)0x0F00).CopyTo(data, MftRunOffset + 0x14);
                break;
        }

        var outcome = ParseImage(data, data.Length);
        Assert.Contains("malformed attribute", outcome.ErrorMessage);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "one run that wraps the byte count")]
    [DataRow(1, DisplayName = "one run before the start of the volume")]
    [DataRow(2, DisplayName = "runs whose sum with the base wraps")]
    public void ParseMFTRecords_ExtensionRunsOutsideTheVolume_FailTheParseBeforeAnotherLookup(int shape)
    {
        var runs = shape switch
        {
            0 => new[] { new ForgedRun(RunLengthThatWrapsTheByteCount, 1, 7, 1) },
            1 => new[] { new ForgedRun(3, -1, 1, 1) },
            _ => Enumerable.Repeat(new ForgedRun(1UL << 50, 0, 7, 1), 4).ToArray()
        };
        var data = BuildRecordZeroImage(out var next);
        // Segment 1032 lies past the base run, so a lookup that used the forged runs would read again.
        next += WriteResidentAttributeList(data, next, AttributeListSegmentTwo, 1032);
        WriteEndMarker(data, next);
        WriteFileRecord(data, ExtensionRecordOffset, 3);
        var attribute = ExtensionRecordOffset + 0x38;
        var length = WriteNonResidentAttribute(data, attribute, 0x80, 4096, EncodeRuns(runs));
        WriteEndMarker(data, attribute + length);

        // Reads: boot sector, record 0, segment 2. The fourth read, the lookup of 1032, would fire the
        // countdown; if the parse stops first, the countdown is still armed and fails the next parse.
        AssertRejected(data, "the merged runs do not describe a $MFT inside the volume", failingRead: 4);
        var control = BuildMultiBatchImage();
        var nextParse = ParseImage(control, control.Length);
        Assert.IsFalse(string.IsNullOrEmpty(nextParse.ErrorMessage),
            "a rejected extension must not drive another read");
    }

    [TestMethod]
    public void ParseMFTRecords_ExtensionAttributeLongerThanItsRecord_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next);
        next += WriteResidentAttributeList(data, next, AttributeListSegmentTwo);
        WriteEndMarker(data, next);
        WriteFileRecord(data, ExtensionRecordOffset, 3);
        var attribute = ExtensionRecordOffset + 0x38;
        WriteNonResidentDataAttribute(data, attribute, 4096, 300, 1);
        BitConverter.GetBytes(0x2000).CopyTo(data, attribute + 4);
        AssertInvalidExtensionInput(data, "MFT extension record has a malformed attribute");
    }

    [TestMethod]
    public void ParseMFTRecords_ExtensionAttributeChainWithoutEndMarker_FailsTheParse()
    {
        var data = BuildRecordZeroImage(out var next);
        next += WriteResidentAttributeList(data, next, AttributeListSegmentTwo);
        WriteEndMarker(data, next);
        WriteFileRecord(data, ExtensionRecordOffset, 3);
        for (var attribute = ExtensionRecordOffset + 0x38; attribute + 0x20 <= ExtensionRecordOffset + 1024;
             attribute += 0x20)
        {
            data[attribute] = 0x30;
            data[attribute + 4] = 0x20;
        }

        AssertInvalidExtensionInput(data, "MFT extension record has a malformed attribute");
    }

    private static void AssertIncompleteExtensionInput(byte[] image) =>
        AssertInvalidExtensionInput(image, "The volume could not be read completely.");

    private static void AssertInvalidExtensionInput(byte[] image, string message)
    {
        var outcome = ParseImage(image, image.Length);
        AssertParseFailedWithNoOutput(outcome);
        Assert.AreEqual(message, outcome.ErrorMessage);
    }

    [TestMethod]
    [DataRow(0u, DisplayName = "no records per chunk")]
    [DataRow(uint.MaxValue, DisplayName = "more bytes per chunk than a DWORD holds")]
    public void ParseMFTRecords_ChunkSizeOutsideWhatTheReaderCanIssue_ParsesTheWholeMft(uint bufferSizeRecords)
    {
        var image = BuildMultiBatchImage();
        var outcome = ParseImage(image, image.Length, bufferSizeRecords: bufferSizeRecords);
        Assert.AreEqual(string.Empty, outcome.ErrorMessage);
        Assert.AreEqual((ulong)BaseMftRecords, outcome.TotalRecords);
    }
}
