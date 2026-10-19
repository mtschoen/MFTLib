using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The walk's pruning rules: a pruned directory is written as a row and has no children, the root is never
///     pruned, a path outside the root has no effect, and names and paths follow the drive comparison.
///     The fixture tree is root, Documents (readme.md, Projects/report.pdf) and Pictures (holiday.jpg).
/// </summary>
public sealed partial class EnumerationProducerTests
{
    static readonly bool DriveComparisonIgnoresCase = OperatingSystem.IsWindows();

    string TreePath(params string[] parts) => Path.Combine([_treeRoot, .. parts]);

    async Task AssertProduced(DirectoryWalkOptions walk, string[] present, string[] absent, string? rootDirectory = null)
    {
        var snapshot = Produce(out _, out _, directoryWalk: walk, rootDirectory: rootDirectory);
        try
        {
            foreach (var path in present)
            {
                Assert.IsTrue(EnumerationLookupTestAccess.FindByPath(snapshot, path).HasValue, $"{path} must be a row");
            }

            foreach (var path in absent)
            {
                Assert.IsFalse(EnumerationLookupTestAccess.FindByPath(snapshot, path).HasValue, $"{path} must not be a row");
            }
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public Task Produce_PrunedDirectoryName_RecordsTheDirectoryButNotItsChildren() =>
        AssertProduced(new DirectoryWalkOptions { PrunedDirectoryNames = ["Documents"] },
            present: [TreePath("Documents"), TreePath("Pictures", "holiday.jpg")],
            absent: [TreePath("Documents", "readme.md"), TreePath("Documents", "Projects")]);

    [TestMethod]
    public async Task Produce_PrunedDirectory_IsARowWithNoChildren()
    {
        var snapshot = Produce(out var block, out var result,
            directoryWalk: new DirectoryWalkOptions { PrunedDirectoryPaths = [TreePath("Documents")] });
        try
        {
            // Root, Documents, Pictures, holiday.jpg.
            Assert.AreEqual(4u, result.RowCount);
            var documents = EnumerationLookupTestAccess.FindByPath(snapshot, TreePath("Documents"));
            Assert.IsTrue(documents.HasValue);
            Assert.IsTrue(documents.GetValueOrDefault().IsDirectory);
            Assert.AreEqual(0, documents.GetValueOrDefault().Children(CancellationToken.None).Count);
            Assert.AreEqual(4u, block.Header.RowCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public Task Produce_PrunedPath_PrunesOnlyTheDirectoryWhosePathEqualsIt() =>
        AssertProduced(new DirectoryWalkOptions { PrunedDirectoryPaths = [TreePath("Documents", "Projects") + Path.DirectorySeparatorChar] },
            present: [TreePath("Documents", "Projects"), TreePath("Documents", "readme.md"), TreePath("Pictures", "holiday.jpg")],
            absent: [TreePath("Documents", "Projects", "report.pdf")]);

    [TestMethod]
    public Task Produce_TheRootIsNeverPruned() =>
        AssertProduced(new DirectoryWalkOptions
        {
            PrunedDirectoryNames = [Path.GetFileName(_treeRoot)],
            PrunedDirectoryPaths = [_treeRoot],
            PruneDotDirectories = true
        },
            present: [TreePath("Documents", "Projects", "report.pdf"), TreePath("Pictures", "holiday.jpg")],
            absent: []);

    [TestMethod]
    public Task Produce_APrunedPathOutsideTheRoot_HasNoEffect() =>
        AssertProduced(new DirectoryWalkOptions { PrunedDirectoryPaths = [TreePath("Pictures")] },
            present: [TreePath("Documents", "Projects", "report.pdf")],
            absent: [],
            rootDirectory: TreePath("Documents"));

    [TestMethod]
    public async Task Produce_NamesFollowTheDriveComparison()
    {
        var walk = new DirectoryWalkOptions { PrunedDirectoryNames = ["DOCUMENTS"] };
        string[] children = [TreePath("Documents", "readme.md")];

        await AssertProduced(walk, present: DriveComparisonIgnoresCase ? [] : children,
            absent: DriveComparisonIgnoresCase ? children : []);
    }

    [TestMethod]
    public async Task Produce_PathsFollowTheDriveComparison()
    {
        var walk = new DirectoryWalkOptions { PrunedDirectoryPaths = [TreePath("PICTURES")] };
        string[] children = [TreePath("Pictures", "holiday.jpg")];

        await AssertProduced(walk, present: DriveComparisonIgnoresCase ? [] : children,
            absent: DriveComparisonIgnoresCase ? children : []);
    }

    [TestMethod]
    public async Task Produce_HiddenAttribute_PrunesHiddenDirectories()
    {
        // Hidden is an attribute bit on Windows and the dot-name convention elsewhere, which .NET reports as Hidden.
        var hidden = TreePath(".cache");
        Directory.CreateDirectory(hidden);
        await File.WriteAllTextAsync(Path.Combine(hidden, "blob.bin"), "x");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }

        await AssertProduced(new DirectoryWalkOptions { PrunedDirectoryAttributes = FileAttributes.Hidden },
            present: [hidden, TreePath("Documents", "readme.md")],
            absent: [Path.Combine(hidden, "blob.bin")]);
    }

    [TestMethod]
    public async Task Produce_PruneDotDirectories_PrunesDotNamedDirectoriesButKeepsDotFiles()
    {
        var gitDirectory = TreePath(".git");
        Directory.CreateDirectory(gitDirectory);
        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/main");
        await File.WriteAllTextAsync(TreePath(".gitignore"), "bin/");

        await AssertProduced(new DirectoryWalkOptions { PruneDotDirectories = true },
            present: [gitDirectory, TreePath(".gitignore"), TreePath("Documents", "readme.md")],
            absent: [Path.Combine(gitDirectory, "HEAD")]);
    }

    [TestMethod]
    public async Task Produce_WithoutRules_EntersHiddenAndDotDirectories()
    {
        var gitDirectory = TreePath(".git");
        Directory.CreateDirectory(gitDirectory);
        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "HEAD"), "ref: refs/heads/main");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(gitDirectory, File.GetAttributes(gitDirectory) | FileAttributes.Hidden);
        }

        await AssertProduced(new DirectoryWalkOptions(), present: [Path.Combine(gitDirectory, "HEAD")], absent: []);
    }

    [TestMethod]
    public void Produce_StoresTheAccessDeniedCountInTheHeader()
    {
        var created = CreateBlock();
        using var block = created.Block;
        var producer = new EnumerationProducer(new EnumerationProducerOptions
        {
            RootDirectory = TreePath("absent"),
            Drive = 'T'
        });

        var result = producer.Produce(created.Writer, progress: null, CancellationToken.None);

        Assert.AreEqual(1, result.AccessDeniedSubtreeCount);
        Assert.AreEqual(1u, block.Header.AccessDeniedSubtreeCount);
    }
}
