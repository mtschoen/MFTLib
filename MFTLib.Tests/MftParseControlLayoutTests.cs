using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The managed mirror of the native MftParseControl must keep the native layout: two 32-bit fields
// at offsets 0 and 4, naturally aligned, because the parser reads both as shared 32-bit loads.
[TestClass]
public class MftParseControlLayoutTests
{
    [StructLayout(LayoutKind.Sequential)]
    struct AlignmentProbe
    {
        public byte Leading;
        public MftParseControl Control;
    }

    [TestMethod]
    public void MftParseControl_MatchesTheNativeLayout()
    {
        Assert.AreEqual(8, Marshal.SizeOf<MftParseControl>());
        Assert.AreEqual(0, (int)Marshal.OffsetOf<MftParseControl>(nameof(MftParseControl.CancelRequested)));
        Assert.AreEqual(4, (int)Marshal.OffsetOf<MftParseControl>(nameof(MftParseControl.ParseThreadAllowance)));
    }

    [TestMethod]
    public void MftParseControl_IsAlignedLikeA32BitField()
    {
        Assert.AreEqual(sizeof(int), (int)Marshal.OffsetOf<AlignmentProbe>(nameof(AlignmentProbe.Control)));
    }
}
