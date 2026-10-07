using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Paths of an MFT dump are virtual: rendered with one separator, looked up with either, and
///     never routed through the host's path rules, so each test passes on Windows and Linux alike.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftDumpPathTests
{
    const string BackslashRoot = "dump:\\D";

    [TestMethod]
    public async Task Navigation_RootParentChildrenAndSubtreeUseTheVirtualRoot()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        var root = index.Root('D');
        var documents = index.Find("dump:/D/documents")!.Value;

        Assert.AreEqual("dump:/D", root.Path);
        Assert.AreEqual("dump:/D/documents", documents.Path);
        Assert.AreEqual(root, documents.Parent);
        CollectionAssert.AreEquivalent(new[] { "Deep", "Notes.txt" }, documents.Children().Select(c => c.Name).ToArray());
        var subtree = index.Search(new SearchQuery("leaf", Under: documents));
        Assert.AreEqual("dump:/D/documents/Deep/leaf.txt", subtree.Single().Path);
    }

    [TestMethod]
    public async Task Find_RoundTripsEveryRenderedPath()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        foreach (var entry in index.Search(new SearchQuery("")).Where(entry => entry.Name != "$MFT"))
        {
            var found = index.Find(entry.Path);
            Assert.IsNotNull(found, entry.Path);
            Assert.AreEqual(entry, found.Value, entry.Path);
        }
    }

    [TestMethod]
    public async Task Find_AcceptsEitherSeparatorAnyCaseAndATrailingSeparator()
    {
        await using var index = await MftDumpIndexes.OpenAsync();
        var leaf = index.Find("dump:/D/documents/Deep/leaf.txt")!.Value;

        string[] spellings =
        [
            "dump:/D/documents/Deep/leaf.txt",
            BackslashRoot + "\\documents\\Deep\\leaf.txt",
            "dump:/D/documents\\Deep/leaf.txt",
            "DUMP:/d/DOCUMENTS/deep/LEAF.TXT",
            "dump:/D/documents/Deep/leaf.txt/",
            "dump:/D//documents///Deep/leaf.txt"
        ];

        foreach (var spelling in spellings)
        {
            Assert.AreEqual(leaf, index.Find(spelling), spelling);
        }
    }

    [TestMethod]
    public async Task Find_ARootWithATrailingSeparatorOrBackslashIsTheRootRow()
    {
        await using var index = await MftDumpIndexes.OpenAsync();
        var root = index.Root('D');

        Assert.AreEqual(root, index.Find("dump:/D"));
        Assert.AreEqual(root, index.Find("dump:/D/"));
        Assert.AreEqual(root, index.Find(BackslashRoot));
        Assert.AreEqual(root, index.Find(BackslashRoot + "\\"));
    }

    [TestMethod]
    public async Task Find_ANearPrefixOrMissingNameIsNull()
    {
        await using var index = await MftDumpIndexes.OpenAsync();

        Assert.IsNull(index.Find("dump:/DX/documents"));
        Assert.IsNull(index.Find("dump:/Dumps"));
        Assert.IsNull(index.Find("dump:/"));
        Assert.IsNull(index.Find("dump:/D/missing"));
        Assert.IsNull(index.Find("dump:/D/documents/Deep/leaf.txt/extra"));
        Assert.IsNull(index.Find(Path.Combine(Path.GetTempPath(), "documents")));
    }

    [TestMethod]
    public async Task Open_AnEntryOfADumpRefusesBeforeAnyByIdOrPathSeam()
    {
        using var restore = FileEntry.OverrideOpenByIdForTest((_, _, _, _) =>
            throw new AssertFailedException("A dump entry must not reach the by-id open."));
        await using var index = await MftDumpIndexes.OpenAsync();

        foreach (var path in new[] { "dump:/D", "dump:/D/documents", "dump:/D/documents/Notes.txt" })
        {
            var failure = MftDumpIndexes.Throws<InvalidOperationException>(
                index, i => i.Find(path)!.Value.Open(FileAccess.Read));
            Assert.AreEqual("Files in an MFT dump cannot be opened through the index.", failure.Message, path);
        }
    }

    [TestMethod]
    public void Paths_CanonicalRootJoinAndPrefixRules()
    {
        Assert.AreEqual("dump:/Q", MftDumpPaths.CanonicalRoot('q'));
        Assert.IsTrue(MftDumpPaths.IsCanonicalRoot("dump:/Q", 'q'));
        Assert.IsFalse(MftDumpPaths.IsCanonicalRoot("dump:/q", 'q'));
        Assert.IsFalse(MftDumpPaths.IsCanonicalRoot(null, 'q'));
        Assert.AreEqual("dump:/Q/a/b", MftDumpPaths.Join("dump:/Q", ["a", "b"]));
        Assert.AreEqual("dump:/Q", MftDumpPaths.Join("dump:/Q/", []));
        Assert.AreEqual("dump:/Q", MftDumpPaths.ToMatchablePrefix(BackslashRoot.Replace('D', 'Q') + "\\"));
        Assert.IsNull(MftDumpPaths.ToMatchablePrefix(null));
        Assert.IsNull(MftDumpPaths.ToMatchablePrefix(string.Empty));
        Assert.IsTrue(MftDumpPaths.IsSeparator('/'));
        Assert.IsTrue(MftDumpPaths.IsSeparator(BackslashRoot[5]));
        Assert.IsFalse(MftDumpPaths.IsSeparator(':'));
    }

    [TestMethod]
    public void Identity_NormalizesThePathAndTheKeyAndRejectsBadInput()
    {
        var identity = new MftDumpSourceIdentity("relative.mft", 'q');

        Assert.AreEqual(Path.GetFullPath("relative.mft"), identity.DumpFilePath);
        Assert.AreEqual('Q', identity.DriveLetter);
        Assert.AreEqual("dump:/Q", identity.Root);
        Assert.ThrowsException<ArgumentException>(() => new MftDumpSourceIdentity(" ", 'D'));
        Assert.ThrowsException<ArgumentException>(() => new MftDumpSourceIdentity("x.mft", '1'));
    }
}
