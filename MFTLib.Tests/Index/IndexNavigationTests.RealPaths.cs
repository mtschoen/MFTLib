using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class IndexNavigationTests
{
    [TestMethod]
    public async Task Path_RendersTheBlockRootDirectory_NotTheDriveKey()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        var documents = builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var readme = builder.AddRow("readme.md", documents, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var rootDirectory = Path.Combine(Path.GetTempPath(), "mftlib-path-root");
        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: rootDirectory)]);
        try
        {
            Assert.AreEqual(rootDirectory, FileEntry.Create(snapshot, 0, root).Path);
            Assert.AreEqual(Path.Combine(rootDirectory, "Documents", "readme.md"),
                FileEntry.Create(snapshot, 0, readme).Path);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     A bare drive specifier such as <c>C:</c> is drive-relative: joining it with a name
    ///     chain yields <c>C:Documents</c>, which Windows resolves against that drive's
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
        var driveBlock = new DriveBlock('C', 0, block, rootDirectoryPath: "C:");
        var snapshot = Snapshot.Create([driveBlock]);
        try
        {
            Assert.AreEqual($"C:{separator}", driveBlock.RootDirectoryPath);

            var path = FileEntry.Create(snapshot, 0, readme).Path;
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
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, block)]);
        try
        {
            var exception = Assert.ThrowsException<InvalidOperationException>(
                () => _ = FileEntry.Create(snapshot, 0, orphan).Path);
            StringAssert.Contains(exception.Message, "root directory");
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
