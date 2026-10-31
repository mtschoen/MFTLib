using System.Runtime.InteropServices;
using System.Text;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class NameHashTests
{
    static ulong ReferenceHash(string name)
    {
        var hash = 14695981039346656037UL;
        for (var index = 0; index < name.Length;)
        {
            var consumed = NameFolding.FoldNext(name, index, out var first, out var second);
            var units = consumed == 2 ? new[] { first, second } : new[] { first };
            foreach (var unit in units)
            {
                hash = unchecked((hash ^ (byte)unit) * 1099511628211UL);
                hash = unchecked((hash ^ (byte)(unit >> 8)) * 1099511628211UL);
            }

            index += consumed;
        }

        return hash;
    }

    static IEnumerable<string> HashInputs()
    {
        yield return "";
        for (var unit = 0; unit < 128; unit++)
        {
            yield return ((char)unit).ToString();
        }

        yield return "Makefile";
        yield return "README.md";
        yield return "aZ09_-.{|}~`@[\\]^ \u007F\u0000";
        yield return "abcdefghijklmnopqrstuvwxyz";
        yield return "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        yield return "café";
        yield return "ßetaıİ";
        yield return "中文abc";
        yield return "a😀B";
        yield return "𐐨";
        yield return "𐐀𐐨";
        yield return "\uD800a\uDC00b";
        yield return "ab\uD800";
        yield return "\uDC00";
    }

    [TestMethod]
    public void Compute_Span_MatchesReferenceFoldForBroadInputs()
    {
        foreach (var input in HashInputs())
        {
            Assert.AreEqual(ReferenceHash(input), NameHash.Compute(input), input);
        }
    }

    [TestMethod]
    public void Compute_NameView_MatchesSpanForBothEncodings()
    {
        foreach (var input in HashInputs())
        {
            var wide = new NameView(MemoryMarshal.AsBytes(input.AsSpan()), isUtf16: true);
            Assert.AreEqual(NameHash.Compute(input), NameHash.Compute(wide), input);
            if (Ascii.IsValid(input))
            {
                var narrow = new NameView(Encoding.ASCII.GetBytes(input), isUtf16: false);
                Assert.AreEqual(NameHash.Compute(input), NameHash.Compute(narrow), input);
            }
        }
    }

    [TestMethod]
    public void Compute_PinsKnownConstants()
    {
        Assert.AreEqual(14695981039346656037UL, NameHash.Compute(""));
        Assert.AreEqual(650948300150757124UL, NameHash.Compute("a"));
        Assert.AreEqual(NameHash.Compute("README.MD"), NameHash.Compute("readme.md"));
    }
}
