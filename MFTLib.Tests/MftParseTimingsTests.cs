using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftParseTimingsTests
{
    [TestMethod]
    public void Constructor_StoresAllValuesAsTimeSpans()
    {
        var t = new MftParseTimings(1.5, 2.5, 3.5, 7.5);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1.5), t.NativeIo);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2.5), t.NativeFixup);
        Assert.AreEqual(TimeSpan.FromMilliseconds(3.5), t.NativeParse);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7.5), t.NativeTotal);
    }

    [TestMethod]
    public void Default_IsAllZero()
    {
        var t = default(MftParseTimings);
        Assert.AreEqual(TimeSpan.Zero, t.NativeIo);
        Assert.AreEqual(TimeSpan.Zero, t.NativeTotal);
    }

    [TestMethod]
    public void ToString_ContainsAllTimings()
    {
        var t = new MftParseTimings(10.1, 20.2, 30.3, 60.6);
        var s = t.ToString();

        Assert.IsTrue(s.Contains("10.1"));
        Assert.IsTrue(s.Contains("20.2"));
        Assert.IsTrue(s.Contains("30.3"));
        Assert.IsTrue(s.Contains("60.6"));
    }
}
