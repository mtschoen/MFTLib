using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#143's two named cases: nested indexed roots resolve to the longest matching root,
///     and child matching follows the block's case rule rather than one global rule.
/// </summary>
[TestClass]
public class LookupEngineRootMatchingTests
{
    static readonly DateTime Moment = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    static SyntheticBlockBuilder BuildSingleFileBlock(char driveLetter, string fileName)
    {
        var builder = new SyntheticBlockBuilder(driveLetter);
        var root = builder.AddRoot();
        builder.AddRow(fileName, root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);
        return builder;
    }

    [TestMethod]
    public void Find_NestedIndexedRoots_ResolvesToTheLongestMatchingRoot()
    {
        var outerRoot = Path.Combine(Path.GetTempPath(), "mftlib-outer");
        var innerRoot = Path.Combine(outerRoot, "inner");

        using var outerBuilder = BuildSingleFileBlock('O', "outer.txt");
        using var innerBuilder = BuildSingleFileBlock('I', "inner.txt");
        var snapshot = Snapshot.Create([
            new DriveBlock('O', 0, outerBuilder.OpenForReading(out _)!, rootDirectoryPath: outerRoot),
            new DriveBlock('I', 1, innerBuilder.OpenForReading(out _)!, rootDirectoryPath: innerRoot)
        ]);
        try
        {
            var inner = LookupEngineTestAccess.Find(snapshot, Path.Combine(innerRoot, "inner.txt"));
            Assert.IsTrue(inner.HasValue);
            Assert.AreEqual('I', inner.Value.Id.DriveLetter);

            var outer = LookupEngineTestAccess.Find(snapshot, Path.Combine(outerRoot, "outer.txt"));
            Assert.IsTrue(outer.HasValue);
            Assert.AreEqual('O', outer.Value.Id.DriveLetter);

            // "inner.txt" is not a child of the outer block's root, so the longest root having
            // won is exactly what makes this resolve at all.
            Assert.IsNull(LookupEngineTestAccess.Find(snapshot,
                Path.Combine(innerRoot, "absent.txt")));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_AnMftBlockMatchesChildNamesCaseInsensitivelyOnEitherPlatform()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        var block = builder.OpenForReading(out _)!;
        var root = TestDriveRoot.For('T');
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: root)]);
        try
        {
            Assert.IsNotNull(LookupEngineTestAccess.Find(snapshot,
                Path.Combine(root, "documents", "notes.txt")));
            Assert.IsNotNull(LookupEngineTestAccess.Find(snapshot,
                Path.Combine(root, "DOCUMENTS", "NOTES.TXT")));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_AnEnumerationBlockFollowsTheHostCaseRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "mftlib-case");
        using var builder = BuildSingleFileBlock('E', "Readme.md");
        var snapshot = Snapshot.Create([
            new DriveBlock('E', 0, builder.OpenForReading(out _)!, rootDirectoryPath: root)
        ]);
        try
        {
            Assert.IsNotNull(LookupEngineTestAccess.Find(snapshot, Path.Combine(root, "Readme.md")));

            var mismatchedCase = LookupEngineTestAccess.Find(snapshot, Path.Combine(root, "README.MD"));
            if (OperatingSystem.IsWindows())
            {
                Assert.IsNotNull(mismatchedCase, "a Windows enumeration block folds case the way the host does");
            }
            else
            {
                Assert.IsNull(mismatchedCase, "an enumeration block over a case-sensitive root matches ordinally");
            }
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    /// <summary>
    ///     A block with no root directory can never match a path, so the search steps over it and
    ///     keeps looking. Synthetic blocks are allowed to have no root, so this is reachable.
    /// </summary>
    [TestMethod]
    public void Find_SkipsABlockWithNoRootDirectory_AndResolvesThroughTheRootedBlock()
    {
        var root = TestDriveRoot.For('T');
        using var rootlessBuilder = BuildSingleFileBlock('N', "visible.txt");
        using var rootedBuilder = BuildSingleFileBlock('T', "visible.txt");
        var snapshot = Snapshot.Create([
            new DriveBlock('N', 0, rootlessBuilder.OpenForReading(out _)!),
            new DriveBlock('T', 1, rootedBuilder.OpenForReading(out _)!, rootDirectoryPath: root)
        ]);
        try
        {
            var entry = LookupEngineTestAccess.Find(snapshot, Path.Combine(root, "visible.txt"));
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual('T', entry.Value.Id.DriveLetter);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_MultiSegmentRootAcceptsMixedSeparatorsThroughoutThePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "mftlib-mixed", "Users", "test");
        using var builder = BuildSingleFileBlock('E', "file.txt");
        var snapshot = Snapshot.Create([
            new DriveBlock('E', 0, builder.OpenForReading(out _)!, rootDirectoryPath: root)
        ]);
        try
        {
            var mixedPath = root.Replace('\\', '/').Replace("/Users/", @"\Users/") + @"\file.txt";
            var entry = LookupEngineTestAccess.Find(snapshot, mixedPath);
            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue(entry.HasValue);
                Assert.AreEqual("file.txt", entry.Value.Name);
            }
            else
            {
                Assert.IsNull(entry);
            }
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_OnLinux_LeavesBackslashesInsideFileNamesUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows reserves backslash as a path separator.");
        }

        var root = Path.Combine(Path.GetTempPath(), "mftlib-backslash-name");
        using var builder = new SyntheticBlockBuilder('E');
        var rootRow = builder.AddRoot();
        var directory = builder.AddRow("with", rootRow, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var nestedFile = builder.AddRow("backslash", directory, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        var backslashFile = builder.AddRow(@"with\backslash", rootRow, RowFlags.InUse, 1, Moment,
            sequenceNumber: 0);
        builder.Complete(Moment);

        var snapshot = Snapshot.Create([
            new DriveBlock('E', 0, builder.OpenForReading(out _)!, rootDirectoryPath: root)
        ]);
        try
        {
            var expected = FileEntry.Create(snapshot, 0, backslashFile);
            var resolved = LookupEngineTestAccess.Find(snapshot, expected.Path);

            Assert.IsTrue(resolved.HasValue);
            Assert.AreEqual(expected.Id, resolved.Value.Id);
            Assert.AreNotEqual(nestedFile, IndexNavigationBridge.RowIndexOf(resolved.Value));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Find_StringPrefixRootsRequireAPathBoundary()
    {
        var outerRoot = Path.Combine(Path.GetTempPath(), "mftlib-boundary", "root");
        var secondRoot = outerRoot + "2";
        using var outerBuilder = new SyntheticBlockBuilder('O');
        var rootRow = outerBuilder.AddRoot();
        var misleadingDirectory = outerBuilder.AddRow("2", rootRow,
            RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        outerBuilder.AddRow("file.txt", misleadingDirectory, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        outerBuilder.Complete(Moment);
        using var secondBuilder = BuildSingleFileBlock('I', "file.txt");
        var outerBlock = new DriveBlock('O', 0, outerBuilder.OpenForReading(out _)!, rootDirectoryPath: outerRoot);
        var snapshot = Snapshot.Create([
            outerBlock,
            new DriveBlock('I', 1, secondBuilder.OpenForReading(out _)!, rootDirectoryPath: secondRoot)
        ]);
        var outerOnly = Snapshot.Create([outerBlock]);
        try
        {
            var entry = LookupEngineTestAccess.Find(snapshot, Path.Combine(secondRoot, "file.txt"));
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual('I', entry.Value.Id.DriveLetter);
            Assert.IsNull(LookupEngineTestAccess.Find(outerOnly, Path.Combine(secondRoot, "file.txt")),
                "a string prefix without a path boundary must not select the outer root");
        }
        finally
        {
            outerOnly.ReleaseNow();
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public async Task Find_IndexedRootWithAndWithoutTrailingSeparatorResolvesIdentically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mftlib-trailing-{Guid.NewGuid():N}");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-trailing-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "hello");
        try
        {
            foreach (var indexedRoot in new[] { root, root + Path.DirectorySeparatorChar })
            {
                await using var index = await FileIndex.OpenAsync(new FileIndexOptions
                {
                    Drives = [new IndexedDrive('E', indexedRoot, TestVolumeSerial.GetNext())],
                    CacheDirectory = cacheDirectory,
                    ProducerPolicy = ProducerPolicy.Enumeration
                }, CancellationToken.None);

                var entry = index.Find(Path.Combine(root, "file.txt"));
                Assert.IsTrue(entry.HasValue);
                Assert.AreEqual(Path.Combine(root, "file.txt"), entry.Value.Path);
                Assert.AreEqual(index.Root('E').Id, index.Find(root)!.Value.Id);
                Assert.AreEqual(index.Root('E').Id, index.Find(root + Path.DirectorySeparatorChar)!.Value.Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(cacheDirectory, recursive: true);
        }
    }
}
