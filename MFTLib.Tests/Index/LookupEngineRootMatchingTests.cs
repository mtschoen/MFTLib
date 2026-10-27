using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Root prefix matching and child case-folding rules in <see cref="LookupEngine" />.
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
    public async Task Find_AnMftBlockMatchesChildNamesCaseInsensitivelyOnEitherPlatform()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        var block = builder.OpenForReading(out _)!;
        var root = TestRootDirectory.For('T');
        var rootBlock = new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: root);
        try
        {
            Assert.IsNotNull(LookupEngine.Find(rootBlock,
                Path.Combine(root, "documents", "notes.txt"), TestContext.CancellationToken));
            Assert.IsNotNull(LookupEngine.Find(rootBlock,
                Path.Combine(root, "DOCUMENTS", "NOTES.TXT"), TestContext.CancellationToken));
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Find_AnEnumerationBlockFollowsTheHostCaseRule()
    {
        using var rootDirectory = new TempDirectory("mftlib-case", create: false);
        var root = rootDirectory.Path;
        using var builder = BuildSingleFileBlock('E', "Readme.md");
        var rootBlock = new RootBlock(new VolumeKey('E'), builder.OpenForReading(out _)!, rootDirectoryPath: root);
        try
        {
            Assert.IsNotNull(LookupEngine.Find(rootBlock, Path.Combine(root, "Readme.md"), TestContext.CancellationToken));

            var mismatchedCase = LookupEngine.Find(rootBlock, Path.Combine(root, "README.MD"), TestContext.CancellationToken);
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
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Find_ReturnsNullForBlockWithNoRootDirectory()
    {
        var root = TestRootDirectory.For('T');
        using var rootlessBuilder = BuildSingleFileBlock('N', "visible.txt");
        var rootlessBlock = new RootBlock(new VolumeKey('N'), rootlessBuilder.OpenForReading(out _)!);
        try
        {
            Assert.IsNull(LookupEngine.Find(rootlessBlock, Path.Combine(root, "visible.txt"), TestContext.CancellationToken));
        }
        finally
        {
            await rootlessBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Find_MultiSegmentRootAcceptsMixedSeparatorsThroughoutThePrefix()
    {
        using var rootDirectory = new TempDirectory("mftlib-mixed", create: false);
        var root = Path.Combine(rootDirectory.Path, "Users", "test");
        using var builder = BuildSingleFileBlock('E', "file.txt");
        var rootBlock = new RootBlock(new VolumeKey('E'), builder.OpenForReading(out _)!, rootDirectoryPath: root);
        try
        {
            var mixedPath = root.Replace('\\', '/').Replace("/Users/", @"\Users/") + @"\file.txt";
            var entry = LookupEngine.Find(rootBlock, mixedPath, TestContext.CancellationToken);
            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue(entry.HasValue);
                Assert.AreEqual("file.txt", entry.GetValueOrDefault().Name);
            }
            else
            {
                Assert.IsNull(entry);
            }
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task Find_OnLinux_LeavesBackslashesInsideFileNamesUntouched()
    {
        using var rootDirectory = new TempDirectory("mftlib-backslash-name", create: false);
        var root = rootDirectory.Path;
        using var builder = new SyntheticBlockBuilder('E');
        var rootRow = builder.AddRoot();
        var directory = builder.AddRow("with", rootRow, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var nestedFile = builder.AddRow("backslash", directory, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        var backslashFile = builder.AddRow(@"with\backslash", rootRow, RowFlags.InUse, 1, Moment,
            sequenceNumber: 0);
        builder.Complete(Moment);

        var rootBlock = new RootBlock(new VolumeKey('E'), builder.OpenForReading(out _)!, rootDirectoryPath: root);
        try
        {
            var expected = FileEntry.Create(rootBlock, backslashFile);
            var resolved = LookupEngine.Find(rootBlock, expected.Path, TestContext.CancellationToken);

            Assert.IsTrue(resolved.HasValue);
            var resolvedVal = resolved.GetValueOrDefault();
            Assert.AreEqual(expected.RecordKey, resolvedVal.RecordKey);
            Assert.AreNotEqual(nestedFile, resolvedVal.RowIndex);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Find_StringPrefixRootsRequireAPathBoundary()
    {
        using var rootDirectory = new TempDirectory("mftlib-boundary", create: false);
        var outerRoot = Path.Combine(rootDirectory.Path, "root");
        var secondRoot = outerRoot + "2";
        using var outerBuilder = new SyntheticBlockBuilder('O');
        var rootRow = outerBuilder.AddRoot();
        var misleadingDirectory = outerBuilder.AddRow("2", rootRow,
            RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        outerBuilder.AddRow("file.txt", misleadingDirectory, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        outerBuilder.Complete(Moment);
        var outerBlock = new RootBlock(new VolumeKey('O'), outerBuilder.OpenForReading(out _)!, rootDirectoryPath: outerRoot);
        try
        {
            Assert.IsNull(LookupEngine.Find(outerBlock, Path.Combine(secondRoot, "file.txt"), TestContext.CancellationToken),
                "a string prefix without a path boundary must not select the outer root");
        }
        finally
        {
            await outerBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Find_IndexedRootWithAndWithoutTrailingSeparatorResolvesIdentically()
    {
        using var rootOwner = new TempDirectory("mftlib-trailing");
        using var cacheOwner = new TempDirectory("mftlib-trailing-cache", create: false);
        var root = rootOwner.Path;
        var cacheDirectory = cacheOwner.Path;
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "hello", TestContext.CancellationToken);
        foreach (var indexedRoot in new[] { root, root + Path.DirectorySeparatorChar })
        {
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('E'), indexedRoot, TestVolumeSerial.GetNext(), ProducerKind.Enumeration),
                CacheDirectory = cacheDirectory
            }, CancellationToken.None);

            var entry = index.Find(Path.Combine(root, "file.txt"), TestContext.CancellationToken);
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual(Path.Combine(root, "file.txt"), entry.GetValueOrDefault().Path);
            Assert.AreEqual(index.Root(TestContext.CancellationToken).RecordKey, index.Find(root, TestContext.CancellationToken)!.Value.RecordKey);
            Assert.AreEqual(index.Root(TestContext.CancellationToken).RecordKey, index.Find(root + Path.DirectorySeparatorChar, TestContext.CancellationToken)!.Value.RecordKey);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
