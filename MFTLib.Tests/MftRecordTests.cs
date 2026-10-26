using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordTests
{
    // A non-null string-pool pointer paired with a zero length: MftRecord must never dereference it.
    const nint NonNullEmptyPoolPointer = 1;

    [TestMethod]
    public void Constructor_InUseFlag_SetsProperty()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0001), "test.txt");
        Assert.IsTrue(record.InUse);
        Assert.IsFalse(record.IsDirectory);
    }

    [TestMethod]
    public void Constructor_DirectoryFlag_SetsProperty()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0003), "Documents");
        Assert.IsTrue(record.InUse);
        Assert.IsTrue(record.IsDirectory);
    }

    [TestMethod]
    public void Constructor_NoFlags_NotInUse()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0000), "deleted.txt");
        Assert.IsFalse(record.InUse);
        Assert.IsFalse(record.IsDirectory);
    }

    [TestMethod]
    public void Properties_StoreCorrectValues()
    {
        var record = new MftRecord(42, 10, new MftRecordFields(0x0001), "readme.md");
        Assert.AreEqual(42UL, record.RecordNumber);
        Assert.AreEqual(10UL, record.ParentRecordNumber);
        Assert.AreEqual("readme.md", record.FileName);
    }

    [TestMethod]
    public void FileName_WhenNull_ReturnsEmpty()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0001), fileName: null);
        Assert.AreEqual(string.Empty, record.FileName);
    }

    [TestMethod]
    [DataRow(false, false, false, DisplayName = "Issue655_FreedFileUnknown")]
    [DataRow(false, false, true, DisplayName = "Issue655_FreedFileKnown")]
    [DataRow(false, true, false, DisplayName = "Issue655_FreedDirectoryUnknown")]
    [DataRow(false, true, true, DisplayName = "Issue655_FreedDirectoryKnown")]
    [DataRow(true, false, false, DisplayName = "Issue655_LiveFileUnknown")]
    [DataRow(true, false, true, DisplayName = "Issue655_LiveFileKnown")]
    [DataRow(true, true, false, DisplayName = "Issue655_LiveDirectoryUnknown")]
    [DataRow(true, true, true, DisplayName = "Issue655_LiveDirectoryKnown")]
    public void Factory_PreservesEveryColumn(bool inUse, bool isDirectory, bool sizeKnown)
    {
        var record = MftRecordFactory.Create(new MftRecordTestValues
        {
            RecordNumber = 99,
            ParentRecordNumber = 7,
            InUse = inUse,
            IsDirectory = isDirectory,
            SizeKnown = sizeKnown,
            FileName = "record",
            FileAttributes = FileAttributes.Hidden | FileAttributes.Archive,
            Size = 1234567,
            ModifiedFileTime = MftFixtureTests.ModifiedBaseFileTime,
            SequenceNumber = 37,
            ParentSequenceNumber = 42
        });

        Assert.AreEqual(99UL, record.RecordNumber);
        Assert.AreEqual(7UL, record.ParentRecordNumber);
        Assert.AreEqual(inUse, record.InUse);
        Assert.AreEqual(isDirectory, record.IsDirectory);
        Assert.AreEqual(sizeKnown, record.SizeKnown);
        Assert.AreEqual("record", record.FileName);
        Assert.AreEqual(FileAttributes.Hidden | FileAttributes.Archive, record.FileAttributes);
        Assert.AreEqual(1234567L, record.Size);
        Assert.AreEqual(DateTime.FromFileTimeUtc(MftFixtureTests.ModifiedBaseFileTime), record.ModifiedUtc);
        Assert.AreEqual((ushort)37, record.SequenceNumber);
        Assert.AreEqual((ushort)42, record.ParentSequenceNumber);
    }

    [TestMethod]
    public void Parse_SequenceNumberMatchesSyntheticRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-sequence-{Guid.NewGuid():N}.mft");
        try
        {
            MftVolume.GenerateSyntheticMFT(path, 100, 256);
            var records = DirectParse.ParseFile(path, out _);
            Assert.IsGreaterThanOrEqualTo(10, records.Length, $"Expected at least 10 records, got {records.Length}");

            foreach (var record in records.Take(10))
            {
                Assert.AreEqual((ushort)(record.RecordNumber + 1), record.SequenceNumber,
                    $"record {record.RecordNumber}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public unsafe void CopyFromNative_Record5_WithName_ReadsItsName()
    {
        fixed (char* dotPointer = ".")
        {
            var record = MftRecord.CopyFromNative(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory),
                dotPointer, 1);

            Assert.AreEqual(".", record.FileName);
        }
    }

    [TestMethod]
    public unsafe void CopyFromNative_Record5_ZeroLengthName_ReadsAsDot()
    {
        var record = MftRecord.CopyFromNative(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory),
            (char*)NonNullEmptyPoolPointer, 0);

        Assert.AreEqual(".", record.FileName);
        Assert.AreEqual(5UL, record.RecordNumber);
        Assert.AreEqual(5UL, record.ParentRecordNumber);
        Assert.IsTrue(record.InUse);
        Assert.IsTrue(record.IsDirectory);
    }

    [TestMethod]
    public unsafe void CopyFromNative_Record5_NullPointer_ReadsAsDot()
    {
        var record = MftRecord.CopyFromNative(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), null, 0);

        Assert.AreEqual(".", record.FileName);
    }

    [TestMethod]
    public unsafe void CopyFromNative_NonRecord5_ZeroLengthName_ReadsEmpty()
    {
        var record = MftRecord.CopyFromNative(100, 5, new MftRecordFields(0x0001, FileAttributes.Normal),
            (char*)NonNullEmptyPoolPointer, 0);

        Assert.AreEqual(string.Empty, record.FileName);
    }

    [TestMethod]
    public unsafe void CopyFromNative_CopiesTheName_SoItOutlivesTheNativeBuffer()
    {
        var buffer = "pooled.txt".ToCharArray();
        MftRecord record;
        fixed (char* name = buffer)
        {
            record = MftRecord.CopyFromNative(30, 5, new MftRecordFields(0x0001), name, (ushort)buffer.Length);
        }

        Array.Fill(buffer, 'x');
        Assert.AreEqual("pooled.txt", record.FileName);
    }
}
