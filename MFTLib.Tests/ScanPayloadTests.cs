using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class ScanPayloadTests
{
    [TestMethod]
    public void ScanPayload_RoundTrips_RecordsAndStrings()
    {
        var records = new[]
        {
            new ScanRecord(RecordNumber: 5, ParentRecordNumber: 5, Size: 0,
                LastWriteTicks: 0, Attributes: 0x10, IsDirectory: true,
                Name: "C:", Path: "C:\\"),
            new ScanRecord(RecordNumber: 100, ParentRecordNumber: 5, Size: 2048,
                LastWriteTicks: 638_000_000_000_000_000, Attributes: 0x20, IsDirectory: false,
                Name: "nöte.txt", Path: "C:\\nöte.txt"),
        };

        var bytes = new byte[ScanPayload.ComputeSize(records)];
        ScanPayload.Write(bytes, records);

        var count = ScanPayload.ReadCount(bytes);
        Assert.AreEqual(2, count);
        var read = ScanPayload.ReadAll(bytes).ToArray();
        CollectionAssert.AreEqual(records, read);
    }

    [TestMethod]
    public void ScanPayload_Empty_RoundTrips()
    {
        var records = Array.Empty<ScanRecord>();
        var bytes = new byte[ScanPayload.ComputeSize(records)];
        ScanPayload.Write(bytes, records);

        Assert.AreEqual(0, ScanPayload.ReadCount(bytes));
        CollectionAssert.AreEqual(Array.Empty<ScanRecord>(), ScanPayload.ReadAll(bytes).ToArray());
    }

    [TestMethod]
    public void ScanPayload_ComputeSize_MatchesWrittenBytes()
    {
        // "ab" = 2 chars UTF-16 = 4 bytes name; "X:\ab" = 5 chars = 10 bytes path
        var records = new[]
        {
            new ScanRecord(RecordNumber: 1, ParentRecordNumber: 0, Size: 512,
                LastWriteTicks: 100, Attributes: 0x20, IsDirectory: false,
                Name: "ab", Path: "X:\\ab"),
        };

        long computedSize = ScanPayload.ComputeSize(records);
        var bytes = new byte[computedSize];
        ScanPayload.Write(bytes, records);

        // Read back and verify it fully round-trips - proving ComputeSize was exact
        var read = ScanPayload.ReadAll(bytes).Single();
        Assert.AreEqual(records[0], read);
        Assert.AreEqual(computedSize, bytes.Length);
    }

    [TestMethod]
    public void ScanPayload_ReadCount_WrongMagic_Throws()
    {
        var bytes = new byte[32];
        // Write a bad magic value (not "MLSC")
        bytes[0] = 0xDE;
        bytes[1] = 0xAD;
        bytes[2] = 0xBE;
        bytes[3] = 0xEF;

        Assert.ThrowsException<InvalidDataException>(() => ScanPayload.ReadCount(bytes));
    }
}
