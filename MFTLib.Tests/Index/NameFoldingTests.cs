using System.Text;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NameFoldingTests
{
    [TestMethod]
    public void Fold_EveryAsciiPair_AgreesWithAsciiEqualsIgnoreCase()
    {
        for (var first = 0; first < 128; first++)
        {
            Assert.IsTrue(char.IsAscii(NameFolding.Fold((char)first)));
            for (var second = 0; second < 128; second++)
            {
                var expected = Ascii.EqualsIgnoreCase([(byte)first], [(byte)second]);
                Assert.AreEqual(expected, NameFolding.Fold((char)first) == NameFolding.Fold((char)second), $"{first} {second}");
            }
        }
    }

    [TestMethod]
    public void Fold_EveryBmpUnit_HashesAsItsFold()
    {
        for (var unit = 0; unit <= 0xFFFF; unit++)
        {
            var character = (char)unit;
            Assert.AreEqual(NameHash.Compute([NameFolding.Fold(character)]), NameHash.Compute([character]), $"U+{unit:X4}");
            if (char.IsSurrogate(character))
            {
                Assert.AreEqual(character, NameFolding.Fold(character));
            }
        }
    }

    [TestMethod]
    public void Fold_EverySupplementaryScalar_StaysSupplementaryAndHashesAsItsFold()
    {
        Span<char> pair = stackalloc char[2];
        Span<char> folded = stackalloc char[2];
        for (var scalar = 0x10000; scalar <= 0x10FFFF; scalar++)
        {
            new Rune(scalar).EncodeToUtf16(pair);
            NameFolding.Fold(pair, folded);
            Assert.IsTrue(char.IsHighSurrogate(folded[0]) && char.IsLowSurrogate(folded[1]), $"U+{scalar:X}");
            Assert.AreEqual(NameHash.Compute(folded), NameHash.Compute(pair), $"U+{scalar:X}");
        }
    }

    [TestMethod]
    [DataRow("ſ", "S")]
    [DataRow("café", "CAFÉ")]
    public void Fold_NonAsciiSpellings(string input, string expected)
    {
        var folded = new char[input.Length];
        NameFolding.Fold(input, folded);
        Assert.AreEqual(expected, new string(folded));
    }

    [TestMethod]
    public void Fold_DotlessI_StaysDotlessOnEveryPlatform()
    {
        // .NET special-cases U+0131 so invariant uppercase leaves it alone; only the long s folds into ASCII.
        Assert.AreEqual('ı', NameFolding.Fold('ı'));
        Assert.AreEqual('S', NameFolding.Fold('ſ'));
    }

    [TestMethod]
    public void Fold_UnpairedSurrogatesFoldUnitByUnit()
    {
        const string input = "\uD800a\uDC00b";
        var folded = new char[input.Length];
        NameFolding.Fold(input, folded);
        Assert.AreEqual("\uD800A\uDC00B", new string(folded));
    }

    [TestMethod]
    public void Compute_IsCaseInsensitiveAndStable()
    {
        Assert.AreEqual(NameHash.Compute("Makefile"), NameHash.Compute("MAKEFILE"));
        Assert.AreEqual(650948300150757124UL, NameHash.Compute("a"));
    }
}
