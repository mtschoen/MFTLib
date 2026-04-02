using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class MftParseTimingsTests
{
    [TestMethod]
    public void Constructor_StoresAllValues()
    {
        var t = new MftParseTimings(1000, 1.5, 2.5, 3.5, 7.5, 0.5);
        Assert.AreEqual(1000UL, t.TotalRecords);
        Assert.AreEqual(1.5, t.NativeIoMs);
        Assert.AreEqual(2.5, t.NativeFixupMs);
        Assert.AreEqual(3.5, t.NativeParseMs);
        Assert.AreEqual(7.5, t.NativeTotalMs);
        Assert.AreEqual(0.5, t.MarshalMs);
    }

    [TestMethod]
    public void WithMarshalMs_ReturnsNewTimingsWithUpdatedMarshal()
    {
        var original = new MftParseTimings(500, 1.0, 2.0, 3.0, 6.0, 0.0);
        var updated = original.WithMarshalMs(4.2);

        Assert.AreEqual(500UL, updated.TotalRecords);
        Assert.AreEqual(1.0, updated.NativeIoMs);
        Assert.AreEqual(2.0, updated.NativeFixupMs);
        Assert.AreEqual(3.0, updated.NativeParseMs);
        Assert.AreEqual(6.0, updated.NativeTotalMs);
        Assert.AreEqual(4.2, updated.MarshalMs);
    }

    [TestMethod]
    public void WithMarshalMs_DoesNotMutateOriginal()
    {
        var original = new MftParseTimings(500, 1.0, 2.0, 3.0, 6.0, 0.0);
        _ = original.WithMarshalMs(9.9);
        Assert.AreEqual(0.0, original.MarshalMs);
    }

    [TestMethod]
    public void ToString_ContainsAllTimings()
    {
        var t = new MftParseTimings(12345, 10.1, 20.2, 30.3, 60.6, 5.5);
        var s = t.ToString();

        Assert.IsTrue(s.Contains("10.1"));
        Assert.IsTrue(s.Contains("20.2"));
        Assert.IsTrue(s.Contains("30.3"));
        Assert.IsTrue(s.Contains("60.6"));
        Assert.IsTrue(s.Contains("5.5"));
        Assert.IsTrue(s.Contains("12,345"));
    }
}
