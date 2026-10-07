using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordTests
{
    // A non-null string-pool pointer paired with a zero length: MftRecord must never dereference it.
    static readonly IntPtr NonNullEmptyPoolPointer = 1;

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
    public void Materialize_AlreadyMaterialized_ReturnsSame()
    {
        var record = new MftRecord(42, 10, new MftRecordFields(0x0001), "readme.md");
        var materialized = record.Materialize();

        Assert.AreEqual(record.RecordNumber, materialized.RecordNumber);
        Assert.AreEqual(record.ParentRecordNumber, materialized.ParentRecordNumber);
        Assert.AreEqual(record.FileName, materialized.FileName);
        Assert.AreEqual(record.InUse, materialized.InUse);
        Assert.AreEqual(record.IsDirectory, materialized.IsDirectory);
    }

    [TestMethod]
    public void Materialize_NoName_KeepsAnEmptyName()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0000), fileName: null);
        Assert.AreEqual(string.Empty, record.Materialize().FileName);
    }

    [TestMethod]
    public void Materialize_PreservesAllFields()
    {
        var record = new MftRecord(99, 7, new MftRecordFields(0x0003), "docs");
        var materialized = record.Materialize();
        Assert.AreEqual(99UL, materialized.RecordNumber);
        Assert.AreEqual(7UL, materialized.ParentRecordNumber);
        Assert.IsTrue(materialized.InUse);
        Assert.IsTrue(materialized.IsDirectory);
        Assert.AreEqual("docs", materialized.FileName);
    }

    [TestMethod]
    public void CreateForTest_Materialize_PreservesSequenceNumber()
    {
        var record = MftRecord.CreateForTest(new MftRecordTestValues
        {
            RecordNumber = 99,
            ParentRecordNumber = 7,
            FileName = "record",
            SequenceNumber = 37
        });

        Assert.AreEqual((ushort)37, record.SequenceNumber);
        Assert.AreEqual((ushort)37, record.Materialize().SequenceNumber);
    }

    [TestMethod]
    public void Parse_SequenceNumberMatchesSyntheticRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mftlib-sequence-{Guid.NewGuid():N}.mft");
        try
        {
            MftVolume.GenerateSyntheticMFT(path, 100, 256);
            var records = DirectParse.ParseFile(path, out _);
            Assert.IsTrue(records.Length >= 10, $"Expected at least 10 records, got {records.Length}");

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
    public unsafe void UnmanagedRecord_Record5_WithName_ReadsItsName()
    {
        fixed (char* dotPointer = ".")
        {
            var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), (IntPtr)dotPointer,
                1);

            Assert.AreEqual(".", record.FileName);
            Assert.AreEqual(".", record.Materialize().FileName);
        }
    }

    [TestMethod]
    public void UnmanagedRecord_Record5_ZeroLengthName_ReadsAsDot()
    {
        var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory),
            NonNullEmptyPoolPointer, 0);

        Assert.AreEqual(".", record.FileName);
        var materialized = record.Materialize();
        Assert.AreEqual(".", materialized.FileName);
        Assert.AreEqual(5UL, materialized.RecordNumber);
        Assert.AreEqual(5UL, materialized.ParentRecordNumber);
        Assert.IsTrue(materialized.InUse);
        Assert.IsTrue(materialized.IsDirectory);
    }

    [TestMethod]
    public void UnmanagedRecord_Record5_NullPointer_ReadsAsDot()
    {
        var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), IntPtr.Zero, 0);

        Assert.AreEqual(".", record.FileName);
        Assert.AreEqual(".", record.Materialize().FileName);
    }

    [TestMethod]
    public void UnmanagedRecord_NonRecord5_ZeroLengthName_ReadsEmpty()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0001, FileAttributes.Normal),
            NonNullEmptyPoolPointer, 0);

        Assert.AreEqual(string.Empty, record.FileName);
    }
}
