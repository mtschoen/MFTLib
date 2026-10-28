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
    public void ThrowIfUndefined_NamesTheQueryParameter()
    {
        NameMatching.ThrowIfUndefined(new SearchQuery("a", NameMatchMode.Glob));

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => NameMatching.ThrowIfUndefined(new SearchQuery("a", (NameMatchMode)99)));

        Assert.AreEqual("query", exception.ParamName);
        Assert.StartsWith("Unknown name match mode.", exception.Message);
    }
}
