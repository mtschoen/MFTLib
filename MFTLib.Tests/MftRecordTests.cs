using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordTests
{
    [TestMethod]
    public void Constructor_InUseFlag_SetsProperty()
    {
        var record = new MftRecord(100, 5, 0x0001, "test.txt", null);
        Assert.IsTrue(record.InUse);
        Assert.IsFalse(record.IsDirectory);
    }

    [TestMethod]
    public void Constructor_DirectoryFlag_SetsProperty()
    {
        var record = new MftRecord(100, 5, 0x0003, "Documents", null);
        Assert.IsTrue(record.InUse);
        Assert.IsTrue(record.IsDirectory);
    }

    [TestMethod]
    public void Constructor_NoFlags_NotInUse()
    {
        var record = new MftRecord(100, 5, 0x0000, "deleted.txt", null);
        Assert.IsFalse(record.InUse);
        Assert.IsFalse(record.IsDirectory);
    }

    [TestMethod]
    public void Properties_StoreCorrectValues()
    {
        var record = new MftRecord(42, 10, 0x0001, "readme.md", null);
        Assert.AreEqual(42UL, record.RecordNumber);
        Assert.AreEqual(10UL, record.ParentRecordNumber);
        Assert.AreEqual("readme.md", record.FileName);
    }

    [TestMethod]
    public void ToString_ReturnsFileName()
    {
        var record = new MftRecord(1, 5, 0x0001, "hello.txt", null);
        Assert.AreEqual("hello.txt", record.ToString());
    }

    [TestMethod]
    public void ToString_WithFullPath_ReturnsFullPath()
    {
        var record = new MftRecord(1, 5, 0x0001, "hello.txt", @"C:\Users\hello.txt");
        Assert.AreEqual(@"C:\Users\hello.txt", record.ToString());
    }

    [TestMethod]
    public void FullPath_WhenNull_ReturnsNull()
    {
        var record = new MftRecord(1, 5, 0x0001, "test.txt", null);
        Assert.IsNull(record.FullPath);
    }

    [TestMethod]
    public void FullPath_WhenSet_ReturnsValue()
    {
        var record = new MftRecord(1, 5, 0x0001, "test.txt", @"D:\folder\test.txt");
        Assert.AreEqual(@"D:\folder\test.txt", record.FullPath);
    }

    [TestMethod]
    public void FileName_WhenNull_ReturnsEmpty()
    {
        var record = new MftRecord(1, 5, 0x0001, null, null);
        Assert.AreEqual(string.Empty, record.FileName);
    }

    [TestMethod]
    public void Materialize_AlreadyMaterialized_ReturnsSame()
    {
        var record = new MftRecord(42, 10, 0x0001, "readme.md", @"C:\readme.md");
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
        var record = new MftRecord(1, 5, 0x0000, (string?)null, null);
        var materialized = record.Materialize();
        Assert.AreEqual(string.Empty, materialized.FileName);
        Assert.IsNull(materialized.FullPath);
    }

    [TestMethod]
    public void Materialize_PreservesAllFields()
    {
        var record = new MftRecord(99, 7, 0x0003, "docs", @"C:\Users\docs");
        var m = record.Materialize();
        Assert.AreEqual(99UL, m.RecordNumber);
        Assert.AreEqual(7UL, m.ParentRecordNumber);
        Assert.IsTrue(m.InUse);
        Assert.IsTrue(m.IsDirectory);
        Assert.AreEqual("docs", m.FileName);
        Assert.AreEqual(@"C:\Users\docs", m.FullPath);
    }
}
