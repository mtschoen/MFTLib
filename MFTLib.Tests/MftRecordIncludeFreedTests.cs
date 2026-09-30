using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftRecordIncludeFreedTests
{
    [TestMethod]
    [DataRow("deleted.txt")]
    [DataRow(@"bare\name")]
    public unsafe void UnresolvedPathRow_PreservesBareNameThroughMaterialization(string name)
    {
        MftRecord materialized;
        fixed (char* pointer = name)
        {
            var record = new MftRecord(42, 8, new MftRecordFields(0x4000, sequenceNumber: 19),
                new NativeStrings(IntPtr.Zero, 0, (IntPtr)pointer, (ushort)name.Length), 'C');
            Assert.AreEqual(name, record.FileName);
            Assert.IsNull(record.FullPath);
            Assert.AreEqual(name, record.ToString());
            Assert.IsFalse(record.InUse);
            Assert.AreEqual((ushort)19, record.SequenceNumber);
            materialized = record.Materialize();
        }

        Assert.AreEqual(name, materialized.FileName);
        Assert.IsNull(materialized.FullPath);
        Assert.AreEqual(name, materialized.ToString());
        Assert.IsFalse(materialized.InUse);
        Assert.AreEqual((ushort)19, materialized.SequenceNumber);
    }
}
