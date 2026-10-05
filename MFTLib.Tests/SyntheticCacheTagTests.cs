using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     <see cref="SyntheticCacheTag" /> reads the two components of a <see cref="CacheTag" />, which the
///     type keeps internal. Whole-value equality, which a consumer's policy relies on, stays public.
/// </summary>
[TestClass]
public class SyntheticCacheTagTests
{
    [TestMethod]
    public void Components_AreTheOnesTheTagWasConstructedWith()
    {
        var tag = new CacheTag("GITW", 7);

        Assert.AreEqual(7u, SyntheticCacheTag.GetVersion(tag));
        Assert.AreEqual("GITW", SyntheticCacheTag.GetFourCharacterCode(tag));
    }

    [TestMethod]
    public void Components_OfTheZeroTag_AreZeroAndFourNulCharacters()
    {
        Assert.AreEqual(0u, SyntheticCacheTag.GetVersion(default));
        Assert.AreEqual("\0\0\0\0", SyntheticCacheTag.GetFourCharacterCode(default));
        Assert.AreEqual(new CacheTag("\0\0\0\0", 0), default(CacheTag));
    }

    [TestMethod]
    public void Equality_StillDistinguishesACodeOnlyAndAVersionOnlyDifference()
    {
        var tag = new CacheTag("GITW", 1);

        Assert.AreEqual(tag, new CacheTag("GITW", 1));
        Assert.AreNotEqual(tag, new CacheTag("FILE", 1));
        Assert.AreNotEqual(tag, new CacheTag("GITW", 2));
        Assert.AreNotEqual(tag, default(CacheTag));
        Assert.AreNotEqual(new CacheTag("\0\0\0\0", 1), default(CacheTag));
    }
}
