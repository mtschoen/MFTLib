using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class CacheTagTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("GIT")]
    [DataRow("GITWW")]
    [DataRow("GIT\u00e9")]
    [DataRow("\uff27ITW")]
    public void InvalidFourCcIsRejected(string code)
    {
        Assert.ThrowsException<ArgumentException>(() => new CacheTag(code, 1));
    }

    [TestMethod]
    public void NullFourCcIsRejected()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new CacheTag(null!, 1));
    }

    [TestMethod]
    public void EqualityIncludesEveryByteAndVersion()
    {
        var tag = new CacheTag("GITW", 1);
        Assert.AreEqual(tag, new CacheTag("GITW", 1));
        Assert.AreNotEqual(tag, new CacheTag("GITW", 2));
        Assert.AreNotEqual(tag, new CacheTag("FILE", 1));
        Assert.AreNotEqual(tag, new CacheTag("gitw", 1));
        Assert.AreEqual("GITW", tag.FourCc);
        Assert.AreEqual(1u, tag.Version);
        Assert.AreEqual(default(CacheTag), new CacheTag("\0\0\0\0", 0));
        Assert.AreNotEqual(default(CacheTag), new CacheTag("\0\0\0\0", 1));
        Assert.AreEqual(uint.MaxValue, new CacheTag("GITW", uint.MaxValue).Version);
    }

    [TestMethod]
    public void AsciiControlsAreAcceptedAndEscapedForDiagnostics()
    {
        var tag = new CacheTag("A\n\0\u007f", 0);
        Assert.AreEqual("A\n\0\u007f", tag.FourCc);
        Assert.IsFalse(tag.ToString().Contains('\n'));
        Assert.IsFalse(tag.ToString().Contains('\0'));
        StringAssert.Contains(new CacheTag("GITW", 1).ToString(), "GITW");
        StringAssert.Contains(new CacheTag("GITW", 1).ToString(), "v1");
    }
}
