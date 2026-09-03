using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordTests
{
    // A non-null string-pool pointer paired with a zero length: MftRecord must never dereference it,
    // and a non-null path-slot pointer is how MftResult marks a row as coming from the resolved-path table.
    static readonly IntPtr NonNullEmptyPoolPointer = 1;

    [TestMethod]
    public void Constructor_InUseFlag_SetsProperty()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0001), "test.txt", null);
        Assert.IsTrue(record.InUse);
        Assert.IsFalse(record.IsDirectory);
    }

    [TestMethod]
    public void Constructor_DirectoryFlag_SetsProperty()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0003), "Documents", null);
        Assert.IsTrue(record.InUse);
        Assert.IsTrue(record.IsDirectory);
    }

    [TestMethod]
    public void Constructor_NoFlags_NotInUse()
    {
        var record = new MftRecord(100, 5, new MftRecordFields(0x0000), "deleted.txt", null);
        Assert.IsFalse(record.InUse);
        Assert.IsFalse(record.IsDirectory);
    }

    [TestMethod]
    public void Properties_StoreCorrectValues()
    {
        var record = new MftRecord(42, 10, new MftRecordFields(0x0001), "readme.md", null);
        Assert.AreEqual(42UL, record.RecordNumber);
        Assert.AreEqual(10UL, record.ParentRecordNumber);
        Assert.AreEqual("readme.md", record.FileName);
    }

    [TestMethod]
    public void ToString_ReturnsFileName()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0001), "hello.txt", null);
        Assert.AreEqual("hello.txt", record.ToString());
    }

    [TestMethod]
    public void ToString_WithFullPath_ReturnsFullPath()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0001), "hello.txt", @"C:\Users\hello.txt");
        Assert.AreEqual(@"C:\Users\hello.txt", record.ToString());
    }

    [TestMethod]
    public void FullPath_WhenNull_ReturnsNull()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0001), "test.txt", null);
        Assert.IsNull(record.FullPath);
    }

    [TestMethod]
    public void FullPath_WhenSet_ReturnsValue()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0001), "test.txt", @"D:\folder\test.txt");
        Assert.AreEqual(@"D:\folder\test.txt", record.FullPath);
    }

    [TestMethod]
    public void FileName_WhenNull_ReturnsEmpty()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0001), null, null);
        Assert.AreEqual(string.Empty, record.FileName);
    }

    [TestMethod]
    public void Materialize_AlreadyMaterialized_ReturnsSame()
    {
        var record = new MftRecord(42, 10, new MftRecordFields(0x0001), "readme.md", @"C:\readme.md");
        var materialized = record.Materialize();

        Assert.AreEqual(record.RecordNumber, materialized.RecordNumber);
        Assert.AreEqual(record.ParentRecordNumber, materialized.ParentRecordNumber);
        Assert.AreEqual(record.FileName, materialized.FileName);
        Assert.AreEqual(record.FullPath, materialized.FullPath);
        Assert.AreEqual(record.InUse, materialized.InUse);
        Assert.AreEqual(record.IsDirectory, materialized.IsDirectory);
    }

    [TestMethod]
    public void Materialize_NullPointers_ReturnsSame()
    {
        var record = new MftRecord(1, 5, new MftRecordFields(0x0000), null, null);
        var materialized = record.Materialize();
        Assert.AreEqual(string.Empty, materialized.FileName);
        Assert.IsNull(materialized.FullPath);
    }

    [TestMethod]
    public void Materialize_PreservesAllFields()
    {
        var record = new MftRecord(99, 7, new MftRecordFields(0x0003), "docs", @"C:\Users\docs");
        var materialized = record.Materialize();
        Assert.AreEqual(99UL, materialized.RecordNumber);
        Assert.AreEqual(7UL, materialized.ParentRecordNumber);
        Assert.IsTrue(materialized.InUse);
        Assert.IsTrue(materialized.IsDirectory);
        Assert.AreEqual("docs", materialized.FileName);
        Assert.AreEqual(@"C:\Users\docs", materialized.FullPath);
    }

    [TestMethod]
    public void UnmanagedRecord_Record5_WithResolvePaths_ReturnsDriveRoot()
    {
        var strings = new NativeStrings(IntPtr.Zero, 0, NonNullEmptyPoolPointer, 0);
        var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), strings, 'C');

        Assert.AreEqual(".", record.FileName);
        Assert.AreEqual(@"C:\", record.FullPath);
        Assert.IsTrue(record.InUse);
        Assert.IsTrue(record.IsDirectory);

        var materialized = record.Materialize();
        Assert.AreEqual(".", materialized.FileName);
        Assert.AreEqual(@"C:\", materialized.FullPath);
        Assert.AreEqual(5UL, materialized.RecordNumber);
        Assert.AreEqual(5UL, materialized.ParentRecordNumber);
        Assert.IsTrue(materialized.InUse);
        Assert.IsTrue(materialized.IsDirectory);
    }

    [TestMethod]
    public unsafe void UnmanagedRecord_Record5_WithoutResolvePaths_ReturnsNullFullPath()
    {
        fixed (char* dotPointer = ".")
        {
            var strings = new NativeStrings((IntPtr)dotPointer, 1, IntPtr.Zero, 0);
            var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), strings, 'C');

            Assert.AreEqual(".", record.FileName);
            Assert.IsNull(record.FullPath);

            var materialized = record.Materialize();
            Assert.AreEqual(".", materialized.FileName);
            Assert.IsNull(materialized.FullPath);
        }
    }

    [TestMethod]
    public void UnmanagedRecord_Record5_WithoutResolvePaths_ZeroLengthName_ReturnsNullFullPath()
    {
        var strings = new NativeStrings(NonNullEmptyPoolPointer, 0, IntPtr.Zero, 0);
        var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), strings, 'C');

        Assert.AreEqual(".", record.FileName);
        Assert.IsNull(record.FullPath);

        var materialized = record.Materialize();
        Assert.AreEqual(".", materialized.FileName);
        Assert.IsNull(materialized.FullPath);
    }

    [TestMethod]
    public void UnmanagedRecord_Record5_NullPointers_ReturnsNullFullPath()
    {
        var strings = new NativeStrings(IntPtr.Zero, 0, IntPtr.Zero, 0);
        var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), strings, 'C');

        Assert.AreEqual(".", record.FileName);
        Assert.IsNull(record.FullPath);

        var materialized = record.Materialize();
        Assert.AreEqual(".", materialized.FileName);
        Assert.IsNull(materialized.FullPath);
    }

    [TestMethod]
    public void UnmanagedRecord_Record5_NoDriveLetter_WithResolvePaths_ReturnsBackslash()
    {
        var strings = new NativeStrings(IntPtr.Zero, 0, NonNullEmptyPoolPointer, 0);
        var record = new MftRecord(5, 5, new MftRecordFields(0x0003, FileAttributes.Directory), strings);

        Assert.AreEqual(".", record.FileName);
        Assert.AreEqual(@"\", record.FullPath);

        var materialized = record.Materialize();
        Assert.AreEqual(".", materialized.FileName);
        Assert.AreEqual(@"\", materialized.FullPath);
    }

    [TestMethod]
    public void UnmanagedRecord_NonRecord5_EmptyPath_ReturnsNullFullPath()
    {
        var strings = new NativeStrings(IntPtr.Zero, 0, NonNullEmptyPoolPointer, 0);
        var record = new MftRecord(100, 5, new MftRecordFields(0x0001, FileAttributes.Normal), strings, 'C');

        Assert.AreEqual(string.Empty, record.FileName);
        Assert.IsNull(record.FullPath);
    }
}
