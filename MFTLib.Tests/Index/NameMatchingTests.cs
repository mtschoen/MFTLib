using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NameMatchingTests
{
    [TestMethod]
    public void EqualsName_FoldsCaseByDefault()
    {
        Assert.IsTrue(NameMatching.EqualsName("Readme.MD", "readme.md", caseSensitive: false));
        Assert.IsFalse(NameMatching.EqualsName("Readme.MD", "readme.md", caseSensitive: true));
    }

    [TestMethod]
    public void ContainsSubstring_IsTheDefaultSearchShape()
    {
        Assert.IsTrue(NameMatching.ContainsSubstring("MyReport2026.pdf", "report", caseSensitive: false));
        Assert.IsFalse(NameMatching.ContainsSubstring("MyReport2026.pdf", "report", caseSensitive: true));
        Assert.IsFalse(NameMatching.ContainsSubstring("MyReport2026.pdf", "invoice", caseSensitive: false));
    }

    [TestMethod]
    public void ContainsSubstring_EmptyNeedleMatchesEverything()
    {
        Assert.IsTrue(NameMatching.ContainsSubstring("anything", ReadOnlySpan<char>.Empty, caseSensitive: false));
    }

    [TestMethod]
    public void IsGlobPattern_DetectsStarAndQuestionMark()
    {
        Assert.IsTrue(NameMatching.IsGlobPattern("*.log"));
        Assert.IsTrue(NameMatching.IsGlobPattern("file?.txt"));
        Assert.IsFalse(NameMatching.IsGlobPattern("plain.txt"));
    }

    [TestMethod]
    public void MatchesGlob_HandlesStarPrefixAndSuffix()
    {
        Assert.IsTrue(NameMatching.MatchesGlob("build.log", "*.log", caseSensitive: false));
        Assert.IsTrue(NameMatching.MatchesGlob("build.log", "build.*", caseSensitive: false));
        Assert.IsFalse(NameMatching.MatchesGlob("build.log", "*.txt", caseSensitive: false));
    }

    [TestMethod]
    public void MatchesGlob_HandlesQuestionMarkAsExactlyOneCharacter()
    {
        Assert.IsTrue(NameMatching.MatchesGlob("a1.txt", "a?.txt", caseSensitive: false));
        Assert.IsFalse(NameMatching.MatchesGlob("a12.txt", "a?.txt", caseSensitive: false));
    }

    [TestMethod]
    public void MatchesGlob_HandlesMultipleStars()
    {
        Assert.IsTrue(NameMatching.MatchesGlob("2026-09-02-report.final.pdf", "*report*pdf", caseSensitive: false));
        Assert.IsFalse(NameMatching.MatchesGlob("2026-09-02-report.final.pdf", "*report*docx", caseSensitive: false));
    }

    [TestMethod]
    public void MatchesGlob_TrailingStarMatchesEmptyRemainder()
    {
        Assert.IsTrue(NameMatching.MatchesGlob("report", "report*", caseSensitive: false));
    }

    [TestMethod]
    public void Matches_RoutesGlobPatternsToGlobAndPlainPatternsToSubstring()
    {
        Assert.IsTrue(NameMatching.Matches("build.log", "*.log", caseSensitive: false));
        Assert.IsTrue(NameMatching.Matches("build.log", "uild", caseSensitive: false));
        Assert.IsFalse(NameMatching.Matches("build.log", "*uild", caseSensitive: false));
    }

    [TestMethod]
    public void GetNameHashCode_AgreesWithCaseFoldedEquality()
    {
        Assert.AreEqual(
            NameMatching.GetNameHashCode("Readme.MD", caseSensitive: false),
            NameMatching.GetNameHashCode("readme.md", caseSensitive: false));
        Assert.AreNotEqual(
            NameMatching.GetNameHashCode("Readme.MD", caseSensitive: true),
            NameMatching.GetNameHashCode("readme.md", caseSensitive: true));
    }
}
