using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class SnapshotSwapTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     The reference-counting mechanics of a superseding snapshot are already covered by
    ///     <c>SnapshotTests.TwoSnapshotsOverOneBlock_KeepItMappedUntilBothRelease</c>; what that
    ///     test does not cover is that a <see cref="FileEntry" /> minted from the older snapshot
    ///     still reads real row data while it is the only thing keeping the block mapped, which
    ///     is the part this test adds.
    /// </summary>
    [TestMethod]
    public void HeldFileEntry_ReadsCorrectlyWhileASupersedingSnapshotIsReleased()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        builder.AddRow("kept.txt", root, RowFlags.InUse, 7, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false);
        var oldSnapshot = Snapshot.Create([driveBlock]);
        var handle = FileEntry.Create(oldSnapshot, 0, 1);

        try
        {
            // Models FileIndex.PublishSnapshot: a new snapshot is created over the same block set
            // and the index's own reference to the previous one is released immediately, leaving
            // oldSnapshot as the only thing keeping driveBlock mapped for this held handle.
            Snapshot.Create([driveBlock]).ReleaseNow();

            Assert.AreEqual("kept.txt", handle.Name);
            Assert.AreEqual(7L, handle.Size);
        }
        finally
        {
            // A failed assertion would otherwise skip this and leave the block mapped through the
            // builder's own disposal, which is how one real failure turns into a cascade.
            oldSnapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public async Task RescanTwice_LeavesExactlyOneBlockFileOnDisk()
    {
        var treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(treeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(treeRoot, "Documents", "readme.md"), "hello");

        try
        {
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', treeRoot, 0x0BADF00D)],
                CacheDirectory = cacheDirectory
            }, CancellationToken.None);

            await index.RescanAsync('T', CancellationToken.None);
            await index.RescanAsync('T', CancellationToken.None);

            Assert.AreEqual(1, Directory.GetFiles(cacheDirectory, "*.mlix").Length);
            Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        }
        finally
        {
            foreach (var directory in new[] { treeRoot, cacheDirectory })
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // A just-unmapped block file can stay locked briefly on Windows.
                }
            }
        }
    }
}
