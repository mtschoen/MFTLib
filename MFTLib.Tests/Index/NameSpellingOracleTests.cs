using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NamePool = MFTLibTestExtensions.SyntheticNamePool;

namespace MFTLib.Tests.Index;

/// <summary>
///     Every query path returns each row under the spelling it was written with, and matches exactly when the oracle
///     says: ordinal for case-sensitive queries, ordinal on the fold of both sides for ignore-case queries.
/// </summary>
[TestClass]
public sealed class NameSpellingOracleTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly string[] Directories = ["a", "b", "c", "d"];

    static readonly (string Directory, string Name)[] Input =
    [
        ("a", "Makefile"), ("a", "makefile"),
        ("b", "README.md"), ("c", "readme.md"),
        ("a", "café.txt"), ("b", "CAFÉ.TXT"),
        ("c", "ındex.js"), ("c", "ſetup.py"),
        ("a", "index.js"), ("b", "index.js"),
        ("c", "\uD800lone.bin"),
        ("d", "readme.txt"), ("d", "README.txt"),
        ("d", "Program.cs"), ("d", "Program.csx")
    ];

    static readonly string[] Patterns =
    [
        "makefile", "Makefile", "README.MD", "café", "INDEX", "I", "S", "*.js", "?ndex.*",
        "\uD800lone.bin", "missing", "", "readme", "*.cs"
    ];

    public TestContext TestContext { get; set; } = null!;

    sealed class Fixture : IDisposable
    {
        const uint RootRow = 5;
        readonly SyntheticBlockBuilder _builder;

        internal Fixture(bool populate = true, uint slotCapacity = 256, uint namePoolCapacity = 4096)
        {
            _builder = new SyntheticBlockBuilder(slotCapacity: slotCapacity, namePoolCapacity: namePoolCapacity);
            _builder.MutateHeader((ref header) =>
            {
                header.ProducerKind = ProducerKind.Mft;
                header.RootRow = RootRow;
                header.ScanShape = MFTLibTestExtensions.SyntheticScanShape.Default(ProducerKind.Mft);
            });
            if (populate)
            {
                _builder.AddRowAt(RootRow, ".", new RowColumns(RootRow, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 0));
                var directoryRows = new Dictionary<string, uint>();
                foreach (var directory in Directories)
                {
                    directoryRows[directory] = _builder.AddRow(directory, RootRow, RowFlags.InUse | RowFlags.Directory, 0, Moment, 0);
                }

                foreach (var (directory, name) in Input)
                {
                    var row = _builder.AddRow(name, directoryRows[directory], RowFlags.InUse, 1, Moment, 0);
                    Files.Add((row, name));
                }

                _builder.Complete(Moment);
            }

            Block = _builder.OpenForWriting();
            Root = new RootBlock(new VolumeKey('T'), Block, rootDirectoryPath: TestRootDirectory.For('T'));
        }

        /// <summary>Completes a block whose rows were restored rather than added, which builds its posting base.</summary>
        internal void Complete() => _builder.Complete(Moment);

        internal List<(uint Row, string Name)> Files { get; } = [];

        internal BlockFile Block { get; }

        internal RootBlock Root { get; }

        internal static string PathOf(string directory, string name) =>
            Path.Combine(TestRootDirectory.For('T'), directory, name);

        public void Dispose()
        {
            Root.ReleaseNowAsync().GetAwaiter().GetResult();
            _builder.Dispose();
        }
    }

    static string Fold(string text)
    {
        var folded = new char[text.Length];
        NameFolding.Fold(text, folded);
        return new string(folded);
    }

    static bool OracleMatches(string name, string pattern, NameMatchMode mode, bool caseSensitive)
    {
        var subject = caseSensitive ? name : Fold(name);
        var expected = caseSensitive ? pattern : Fold(pattern);
        return mode switch
        {
            NameMatchMode.Exact => subject == expected,
            NameMatchMode.Substring => subject.Contains(expected, StringComparison.Ordinal),
            _ => OracleGlob(subject, expected, 0, 0)
        };
    }

    /// <summary>A recursive wildcard match, deliberately a different algorithm from the production iteration.</summary>
    static bool OracleGlob(string name, string pattern, int nameIndex, int patternIndex)
    {
        if (patternIndex == pattern.Length)
        {
            return nameIndex == name.Length;
        }

        if (pattern[patternIndex] == '*')
        {
            for (var skip = nameIndex; skip <= name.Length; skip++)
            {
                if (OracleGlob(name, pattern, skip, patternIndex + 1))
                {
                    return true;
                }
            }

            return false;
        }

        return nameIndex < name.Length && (pattern[patternIndex] == '?' || pattern[patternIndex] == name[nameIndex]) &&
               OracleGlob(name, pattern, nameIndex + 1, patternIndex + 1);
    }

    void AssertOracle(RootBlock root, IReadOnlyList<(uint Row, string Name)> files, NameMatchMode mode,
        bool caseSensitive)
    {
        foreach (var pattern in Patterns)
        {
            var expected = files.Where(item => OracleMatches(item.Name, pattern, mode, caseSensitive)).ToArray();
            var query = new SearchQuery(pattern, mode, caseSensitive, IsDirectory: false);
            var searched = SearchEngine.Search(root, query, cancellationToken: TestContext.CancellationToken);
            var streamed = SearchEngine.Enumerate(root, query, cancellationToken: TestContext.CancellationToken).ToArray();
            var rows = NamePool.EnumerateRows(root, query, TestContext.CancellationToken);
            var label = $"{mode} caseSensitive={caseSensitive} pattern='{pattern}'";
            Assert.AreSequenceEqual(expected.Select(item => item.Row).ToArray(), searched.Select(entry => entry.RowIndex).ToArray(), label);
            Assert.AreSequenceEqual(expected.Select(item => item.Name).ToArray(), searched.Select(entry => entry.Name).ToArray(), label);
            Assert.AreSequenceEqual(searched.Select(entry => entry.RowIndex).ToArray(), streamed.Select(entry => entry.RowIndex).ToArray(), label);
            Assert.AreSequenceEqual(expected, rows, label);
        }

    }

    [TestMethod]
    [DataRow(NameMatchMode.Exact, false)]
    [DataRow(NameMatchMode.Exact, true)]
    [DataRow(NameMatchMode.Substring, false)]
    [DataRow(NameMatchMode.Substring, true)]
    [DataRow(NameMatchMode.Glob, false)]
    [DataRow(NameMatchMode.Glob, true)]
    public void EveryQueryPath_ReturnsInputSpellingsMatchingTheOracle(NameMatchMode mode, bool caseSensitive)
    {
        using var fixture = new Fixture();
        AssertOracle(fixture.Root, fixture.Files, mode, caseSensitive);
        Assert.IsNull(NamePool.CheckIntegrity(fixture.Block));
    }

    [TestMethod]
    public void CaseSensitiveSubstring_UsesTheOriginalPattern()
    {
        using var fixture = new Fixture();
        var sensitive = SearchEngine.Search(fixture.Root,
            new SearchQuery("readme", CaseSensitive: true, IsDirectory: false), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(new[] { "readme.md", "readme.txt" }, sensitive.Select(entry => entry.Name).ToArray());
        var insensitive = SearchEngine.Search(fixture.Root,
            new SearchQuery("readme", CaseSensitive: false, IsDirectory: false), cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(4, insensitive.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Glob_MatchesWildcardsNotALiteralSubstring(bool caseSensitive)
    {
        using var fixture = new Fixture();
        var matches = SearchEngine.Search(fixture.Root,
            new SearchQuery("*.cs", NameMatchMode.Glob, caseSensitive, IsDirectory: false), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(new[] { "Program.cs" }, matches.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void Find_IgnoreCase_PrefersTheOrdinalSpellingElseTheLowestRow()
    {
        using var fixture = new Fixture();
        var exact = LookupEngine.Find(fixture.Root, Fixture.PathOf("a", "makefile"), TestContext.CancellationToken);
        Assert.AreEqual("makefile", exact!.Value.Name);
        var upper = LookupEngine.Find(fixture.Root, Fixture.PathOf("a", "Makefile"), TestContext.CancellationToken);
        Assert.AreEqual("Makefile", upper!.Value.Name);
        var neither = LookupEngine.Find(fixture.Root, Fixture.PathOf("a", "MAKEFILE"), TestContext.CancellationToken);
        Assert.AreEqual(fixture.Files[0].Row, neither!.Value.RowIndex);
        Assert.AreEqual("Makefile", neither.Value.Name);
    }

    [TestMethod]
    public void Find_NamesThatDifferOnlyByCaseInDifferentDirectoriesResolveWithinTheirParent()
    {
        using var fixture = new Fixture();
        Assert.AreEqual("README.md", LookupEngine.Find(fixture.Root, Fixture.PathOf("b", "readme.md"), TestContext.CancellationToken)!.Value.Name);
        Assert.AreEqual("readme.md", LookupEngine.Find(fixture.Root, Fixture.PathOf("c", "README.MD"), TestContext.CancellationToken)!.Value.Name);
        Assert.IsNull(LookupEngine.Find(fixture.Root, Fixture.PathOf("a", "readme.md"), TestContext.CancellationToken));
    }

    [TestMethod]
    public void Children_ReturnInputSpellings()
    {
        using var fixture = new Fixture();
        var directory = LookupEngine.Find(fixture.Root, Path.Combine(TestRootDirectory.For('T'), "d"), TestContext.CancellationToken)!.Value;
        var children = IndexNavigation.GetChildren(fixture.Root, directory.RowIndex, TestContext.CancellationToken);
        Assert.AreSequenceEqual(new[] { "readme.txt", "README.txt", "Program.cs", "Program.csx" },
            children.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void CaseOnlyRename_IsVisibleToEveryQueryMode()
    {
        using var fixture = new Fixture();
        var peer = fixture.Files.Single(item => item.Name == "readme.md").Row;
        var renamed = fixture.Files.Single(item => item.Name == "README.md").Row;
        var writer = new BlockWriter(fixture.Block);
        Assert.IsTrue(writer.TryRenameRow(renamed, "readme.md", parentRow: fixture.Block.Rows[(int)renamed].ParentRow));

        Assert.AreEqual("readme.md", FileEntry.Create(fixture.Root, renamed).Name);
        Assert.AreEqual("readme.md", FileEntry.Create(fixture.Root, peer).Name);
        var oldSpelling = SearchEngine.Search(fixture.Root,
            new SearchQuery("README.md", NameMatchMode.Exact, CaseSensitive: true), cancellationToken: TestContext.CancellationToken);
        Assert.IsEmpty(oldSpelling);
        var newSpelling = SearchEngine.Search(fixture.Root,
            new SearchQuery("readme.md", NameMatchMode.Exact, CaseSensitive: true), cancellationToken: TestContext.CancellationToken);
        Assert.AreSequenceEqual(new[] { renamed, peer }.Order().ToArray(),
            newSpelling.Select(entry => entry.RowIndex).Order().ToArray());
        Assert.IsNull(NamePool.CheckIntegrity(fixture.Block));
    }

    [TestMethod]
    public void CaseOnlyRename_ToANewSpelling_LeavesThePeerAndMatchesEveryMode()
    {
        using var fixture = new Fixture();
        var peer = fixture.Files.Single(item => item.Name == "readme.md").Row;
        var renamed = fixture.Files.Single(item => item.Name == "README.md").Row;
        var writer = new BlockWriter(fixture.Block);
        Assert.IsTrue(writer.TryRenameRow(renamed, "ReadMe.md", parentRow: fixture.Block.Rows[(int)renamed].ParentRow));
        fixture.Files[fixture.Files.FindIndex(item => item.Row == renamed)] = (renamed, "ReadMe.md");

        Assert.AreEqual("readme.md", FileEntry.Create(fixture.Root, peer).Name);
        foreach (var mode in Enum.GetValues<NameMatchMode>())
        {
            AssertOracle(fixture.Root, fixture.Files, mode, caseSensitive: false);
            AssertOracle(fixture.Root, fixture.Files, mode, caseSensitive: true);
        }
    }

    [TestMethod]
    public async Task Reopen_PreservesTheOracle()
    {
        using var fixture = new Fixture();
        using var reopened = BlockFile.Open(fixture.Block.Path, fixture.Block.Header.VolumeSerial, out var validation);
        Assert.IsNull(validation);
        Assert.IsNotNull(reopened);
        var rootBlock = new RootBlock(new VolumeKey('T'), reopened, rootDirectoryPath: TestRootDirectory.For('T'));
        try
        {
            foreach (var mode in Enum.GetValues<NameMatchMode>())
            {
                AssertOracle(rootBlock, fixture.Files, mode, caseSensitive: false);
                AssertOracle(rootBlock, fixture.Files, mode, caseSensitive: true);
            }

            Assert.IsNull(NamePool.CheckIntegrity(reopened));
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Growth_PreservesTheOracle()
    {
        using var source = new Fixture();
        var snapshot = BlockRowSnapshot.Capture(source.Block);
        using var grown = new Fixture(populate: false, slotCapacity: 4096, namePoolCapacity: 1 << 20);
        snapshot.RestoreInto(grown.Block);
        grown.Complete();
        grown.Files.AddRange(source.Files);
        foreach (var mode in Enum.GetValues<NameMatchMode>())
        {
            AssertOracle(grown.Root, grown.Files, mode, caseSensitive: false);
            AssertOracle(grown.Root, grown.Files, mode, caseSensitive: true);
        }

        Assert.IsNull(NamePool.CheckIntegrity(grown.Block));
    }
}
