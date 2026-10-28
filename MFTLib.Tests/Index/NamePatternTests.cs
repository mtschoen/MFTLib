using System.Runtime.InteropServices;
using System.Text;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NamePatternTests
{
    static bool Matches(string name, string pattern, NameMatchMode mode, bool caseSensitive)
    {
        var scratch = Array.Empty<char>();
        var view = Ascii.IsValid(name)
            ? new NameView(Encoding.ASCII.GetBytes(name), isUtf16: false)
            : new NameView(MemoryMarshal.AsBytes(name.AsSpan()), isUtf16: true);
        return new NamePattern(pattern, mode, caseSensitive).Matches(view, ref scratch);
    }

    [TestMethod]
    public void Substring_IsTheDefaultSearchShape()
    {
        Assert.IsTrue(Matches("MyReport2026.pdf", "report", NameMatchMode.Substring, false));
        Assert.IsFalse(Matches("MyReport2026.pdf", "report", NameMatchMode.Substring, true));
        Assert.IsFalse(Matches("MyReport2026.pdf", "invoice", NameMatchMode.Substring, false));
    }

    [TestMethod]
    public void ContainsSubstring_EmptyNeedleMatchesEverything()
    {
        Assert.IsTrue(Matches("anything", "", NameMatchMode.Substring, false));
    }

    [TestMethod]
    public void MatchesGlob_HandlesStarPrefixAndSuffix()
    {
        Assert.IsTrue(Matches("build.log", "*.log", NameMatchMode.Glob, false));
        Assert.IsTrue(Matches("build.log", "build.*", NameMatchMode.Glob, false));
        Assert.IsFalse(Matches("build.log", "*.txt", NameMatchMode.Glob, false));
    }

    [TestMethod]
    public void MatchesGlob_HandlesQuestionMarkAsExactlyOneCharacter()
    {
        Assert.IsTrue(Matches("a1.txt", "a?.txt", NameMatchMode.Glob, false));
        Assert.IsFalse(Matches("a12.txt", "a?.txt", NameMatchMode.Glob, false));
    }

    [TestMethod]
    public void MatchesGlob_HandlesMultipleStars()
    {
        Assert.IsTrue(Matches("2026-09-02-report.final.pdf", "*report*pdf", NameMatchMode.Glob, false));
        Assert.IsFalse(Matches("2026-09-02-report.final.pdf", "*report*docx", NameMatchMode.Glob, false));
    }

    [TestMethod]
    public void MatchesGlob_TrailingStarMatchesEmptyRemainder()
    {
        Assert.IsTrue(Matches("report", "report*", NameMatchMode.Glob, false));
    }

    [TestMethod]
    public void Matches_RoutesEachModeToItsComparison()
    {
        Assert.IsTrue(Matches("build.log", "BUILD.LOG", NameMatchMode.Exact, false));
        Assert.IsFalse(Matches("build.log", "uild", NameMatchMode.Exact, false));
        Assert.IsTrue(Matches("build.log", "uild", NameMatchMode.Substring, false));
        Assert.IsTrue(Matches("build.log", "*.log", NameMatchMode.Glob, false));
        Assert.IsFalse(Matches("build.log", "*uild", NameMatchMode.Glob, false));
    }

    [TestMethod]
    public void Matches_ExactAndSubstring_TreatWildcardsAsOrdinaryCharacters()
    {
        Assert.IsTrue(Matches("a*b?.txt", "a*b?", NameMatchMode.Substring, true));
        Assert.IsFalse(Matches("axxb1.txt", "a*b?", NameMatchMode.Substring, true));
        Assert.IsTrue(Matches("a*b?", "a*b?", NameMatchMode.Exact, true));
        Assert.IsFalse(Matches("axb1", "a*b?", NameMatchMode.Exact, true));
    }

    [TestMethod]
    public void Matches_EmptyPattern_FollowsTheModesContract()
    {
        Assert.IsTrue(Matches("", "", NameMatchMode.Exact, false));
        Assert.IsFalse(Matches("a", "", NameMatchMode.Exact, false));
        Assert.IsTrue(Matches("a", "", NameMatchMode.Substring, false));
        Assert.IsTrue(Matches("", "", NameMatchMode.Glob, false));
        Assert.IsFalse(Matches("a", "", NameMatchMode.Glob, false));
    }


    [TestMethod]
    public void Constructor_UndefinedMode_Throws()
    {
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new NamePattern("a", (NameMatchMode)99, caseSensitive: false));

        Assert.AreEqual("mode", exception.ParamName);
        Assert.StartsWith("Unknown name match mode.", exception.Message);
    }

    [TestMethod]
    [DataRow("S", "ſetup.py", NameMatchMode.Substring, true)]
    [DataRow("ſ", "SETUP.PY", NameMatchMode.Substring, true)]
    [DataRow("I", "ındex.js", NameMatchMode.Substring, false)]
    [DataRow("CAFÉ", "café.txt", NameMatchMode.Substring, true)]
    [DataRow("CAFé.TXT", "cafÉ.txt", NameMatchMode.Exact, true)]
    [DataRow("café*", "CAFÉ.TXT", NameMatchMode.Glob, true)]
    [DataRow("é", "cafe", NameMatchMode.Substring, false)]
    [DataRow("*é*", "cafe", NameMatchMode.Glob, false)]
    public void IgnoreCase_AgreesWithTheFoldAcrossWidths(string pattern, string name, NameMatchMode mode, bool expected)
    {
        Assert.AreEqual(expected, Matches(name, pattern, mode, caseSensitive: false));
    }

    [TestMethod]
    public void CaseSensitive_NonAsciiPatternNeverMatchesAnAsciiName()
    {
        Assert.IsFalse(Matches("cafe", "é", NameMatchMode.Substring, caseSensitive: true));
        Assert.IsFalse(Matches("cafe", "café", NameMatchMode.Exact, caseSensitive: true));
    }

    [TestMethod]
    public void CaseSensitive_SubstringIsAnOrdinalSearchOfTheOriginalPattern()
    {
        Assert.IsTrue(Matches("readme.txt", "readme", NameMatchMode.Substring, caseSensitive: true));
        Assert.IsFalse(Matches("README.txt", "readme", NameMatchMode.Substring, caseSensitive: true));
        Assert.IsTrue(Matches("café.txt", "afé", NameMatchMode.Substring, caseSensitive: true));
        Assert.IsFalse(Matches("cafÉ.txt", "afé", NameMatchMode.Substring, caseSensitive: true));
    }

    [TestMethod]
    public void Glob_IsWildcardMatchingNotALiteralSubstring()
    {
        Assert.IsTrue(Matches("Program.cs", "*.cs", NameMatchMode.Glob, caseSensitive: true));
        Assert.IsFalse(Matches("Program.csx", "*.cs", NameMatchMode.Glob, caseSensitive: true));
        Assert.IsFalse(Matches("PROGRAM.CSX", "*.cs", NameMatchMode.Glob, caseSensitive: false));
        Assert.IsTrue(Matches("a*b", "a*b", NameMatchMode.Glob, caseSensitive: true));
    }

    [TestMethod]
    public void Substring_IgnoreCaseAscii_RepeatsFromTheNextPositionAfterAFalseStart()
    {
        Assert.IsTrue(Matches("aaaab.txt", "AAB", NameMatchMode.Substring, caseSensitive: false));
        Assert.IsTrue(Matches("xxABxABC", "abc", NameMatchMode.Substring, caseSensitive: false));
        Assert.IsFalse(Matches("xxABxAB", "abc", NameMatchMode.Substring, caseSensitive: false));
    }

    [TestMethod]
    public void Utf16Entries_ReuseOneGrowingScratchBuffer()
    {
        var scratch = Array.Empty<char>();
        var pattern = new NamePattern("CAFÉ", NameMatchMode.Substring, caseSensitive: false);
        foreach (var name in new[] { "café", "my-café-long-name-with-more-characters" })
        {
            var view = new NameView(MemoryMarshal.AsBytes(name.AsSpan()), isUtf16: true);
            Assert.IsTrue(pattern.Matches(view, ref scratch));
        }

        Assert.IsGreaterThanOrEqualTo(6, scratch.Length);
    }
}
