using System.Globalization;
using MFTLib.Tests.TestSupport;
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

        Assert.Contains("10.1", s);
        Assert.Contains("20.2", s);
        Assert.Contains("30.3", s);
        Assert.Contains("60.6", s);
    }

    [TestMethod]
    public void ToString_UsesInvariantDecimalPointsUnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var s = new MftParseTimings(10.1, 20.2, 30.3, 60.6).ToString();

            Assert.AreEqual("Native: 60.6ms (IO: 10.1ms, Fixup: 20.2ms, Parse: 30.3ms)", s);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void DumpParse_ReportsNonNegativePhaseTimingsBoundedByTheTotal()
    {
        using var directory = new TempDirectory("mftlib-timings");
        var path = MftDumpFixture.WriteFile(directory.Path, MftDumpFixture.Standard());
        using var input = MftDumpInput.Open(path);

        MftParseTimings timings;
        using (var result = input.Parse())
        {
            timings = result.Timings;
        }

        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, timings.NativeIo);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, timings.NativeFixup);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, timings.NativeParse);
        Assert.IsGreaterThanOrEqualTo(timings.NativeIo, timings.NativeTotal);
        Assert.IsGreaterThanOrEqualTo(timings.NativeFixup, timings.NativeTotal);
        Assert.IsGreaterThanOrEqualTo(timings.NativeParse, timings.NativeTotal);
    }
}
