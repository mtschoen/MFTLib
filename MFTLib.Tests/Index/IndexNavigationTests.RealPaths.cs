using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class IndexNavigationTests
{
    [TestMethod]
    [DataRow(false, uint.MaxValue, 1)]
    [DataRow(true, uint.MaxValue, 1)]
    [DataRow(false, uint.MaxValue, 2)]
    [DataRow(true, uint.MaxValue, 2)]
    [DataRow(false, 300u, 1)]
    [DataRow(true, 300u, 1)]
    [DataRow(false, 300u, 2)]
    [DataRow(true, 300u, 2)]
    public async Task Path_OfDetachedDescendants_ExtendsEachParentWithoutTheRoot_Issue886(
        bool isDump, uint missingParentRow, int descendantDepth)
    {
        using var builder = new SyntheticBlockBuilder('D');
        builder.MutateHeader((ref header) => header.ProducerKind = ProducerKind.Mft);
        builder.AddRoot();
        var detached = builder.AddRow("detached", missingParentRow,
            RowFlags.InUse | RowFlags.Directory | RowFlags.Tombstone, 0, Moment, 0);
        var parent = detached;
        string[] names = descendantDepth == 1 ? ["child.bin"] : ["sub", "leaf.txt"];
        for (var level = 0; level < names.Length; level++)
        {
            var flags = level == names.Length - 1 ? RowFlags.InUse : RowFlags.InUse | RowFlags.Directory;
            parent = builder.AddRow(names[level], parent, flags, 0, Moment, 0);
        }
        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        var rootDirectory = isDump ? MftDumpIndexes.Root : builder.DirectoryPath;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('D'), block, rootDirectoryPath: rootDirectory,
            mftSource: isDump ? MftDumpIndexes.Source() : null)];
        try
        {
            var separator = isDump ? '/' : Path.DirectorySeparatorChar;
            var entry = FileEntry.Create(snapshot[0], parent);
            Assert.AreEqual(string.Join(separator, new[] { "detached" }.Concat(names)), entry.Path);
            for (var level = 0; level < descendantDepth; level++)
            {
                Assert.IsNotNull(entry.Parent);
                var ancestor = entry.Parent.Value;
                Assert.AreEqual(ancestor.Path + separator + entry.Name, entry.Path);
                entry = ancestor;
            }
            Assert.AreEqual("detached", entry.Path);
            Assert.IsNull(entry.Parent);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    [DataRow(false, "alias/", "", "資料", "leaf")]
    [DataRow(false, "", "", "a/", "leaf")]
    [DataRow(false, "alias", "before", "/rooted", "leaf")]
    [DataRow(false, "alias", "C:relative", "D:\\rooted", "資料")]
    [DataRow(false, "alias\\", "before\\", "", "leaf")]
    [DataRow(false, "", "", "", "")]
    [DataRow(true, "dump:/T///", "", "資料", "leaf")]
    [DataRow(true, "dump:/T///", "", "", "")]
    [DataRow(true, "dump:/T", "a/", "/b", "leaf")]
    public void PathWriter_PreservesRootAndSeparatorPolicy(bool isDump, string root, params string[] names)
    {
        using var builder = new SyntheticBlockBuilder();
        var row = builder.AddRoot();
        foreach (var name in names)
        {
            row = builder.AddRow(name, row, RowFlags.InUse, 0, Moment, 0);
        }
        builder.Complete(Moment);
        using var block = builder.OpenForReading(out _)!;
        var identifiers = names.Reverse().Select(name =>
        {
            Assert.IsTrue(NamePool.TryFindOrdinal(block, name, out var identifier));
            return identifier;
        }).ToArray();
        var expected = isDump
            ? MftDumpPaths.Join(root, names.Where(name => name.Length != 0).ToArray())
            : Path.Combine([root, .. names]);
        Assert.AreEqual(expected, RootKind.For(ProducerKind.Mft, isDump).JoinPath(block, root, identifiers));
    }

    [TestMethod]
    [DataRow(12)]
    [DataRow(2048)]
    public async Task Path_MixedNamesUnderAnAlias_AllocatesOnlyTheResult_Issue882(int segmentLength)
    {
        using var builder = new SyntheticBlockBuilder(namePoolCapacity: 16384);
        var row = builder.AddRoot();
        var rootDirectory = Path.Combine(builder.DirectoryPath, "Alias資料");
        var components = new List<string> { rootDirectory };
        for (var depth = 0; depth < 12; depth++)
        {
            var name = depth % 2 == 0 ? new string('a', segmentLength) : "資料😀MiXeD";
            row = builder.AddRow(name, row, RowFlags.InUse | RowFlags.Directory, 0, Moment, 0);
            components.Add(name);
        }
        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: rootDirectory)];
        try
        {
            var entry = FileEntry.Create(snapshot[0], row);
            var expected = Path.Combine(components.ToArray());
            Assert.AreEqual(expected, entry.Path);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var actual = entry.Path;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.AreEqual(expected, actual);
            Assert.IsLessThanOrEqualTo(2L * actual.Length + 64, allocated,
                "Path construction must allocate the result, not strings or collections for its ancestors.");
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_RendersTheBlockRootDirectory_NotTheRootKey()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        var documents = builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var readme = builder.AddRow("readme.md", documents, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var rootDirectory = Path.Combine(builder.DirectoryPath, "mftlib-path-root");
        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: rootDirectory)];
        try
        {
            Assert.AreEqual(rootDirectory, FileEntry.Create(snapshot[0], root).Path);
            Assert.AreEqual(Path.Combine(rootDirectory, "Documents", "readme.md"),
                FileEntry.Create(snapshot[0], readme).Path);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     A bare root specifier such as <c>C:</c> is root-relative: joining it with a name
    ///     chain yields <c>C:Documents</c>, which Windows resolves against that root's
    ///     per-process current directory rather than its root, and which
    ///     <c>FileIndex.Find</c> then rejects because the character after the root is not a
    ///     separator. The block normalises the root as it comes in, so the rendered path is
    ///     rooted and the root joins the name chain with exactly one separator.
    /// </summary>
    [TestMethod]
    public async Task Path_ForARootWithoutATrailingSeparator_JoinsWithExactlyOneSeparator()
    {
        using var builder = new SyntheticBlockBuilder('C');
        var root = builder.AddRoot();
        var documents = builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var readme = builder.AddRow("readme.md", documents, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var separator = Path.DirectorySeparatorChar;
        var block = builder.OpenForReading(out _)!;
        var rootBlock = new RootBlock(new VolumeKey('C'), block, rootDirectoryPath: "C:");
        RootBlock[] snapshot = [rootBlock];
        try
        {
            Assert.AreEqual($"C:{separator}", rootBlock.RootDirectoryPath);

            var path = FileEntry.Create(snapshot[0], readme).Path;
            Assert.AreEqual($"C:{separator}Documents{separator}readme.md", path);
            Assert.IsFalse(path.Contains($"{separator}{separator}", StringComparison.Ordinal),
                "the root and the name chain join with exactly one separator");
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_WithNoRootDirectoryOnTheBlock_Throws()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        var orphan = builder.AddRow("orphan.txt", root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('T'), block)];
        try
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => _ = FileEntry.Create(snapshot[0], orphan).Path);
            Assert.Contains("root directory", exception.Message);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
