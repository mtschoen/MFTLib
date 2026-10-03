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
    public async Task HeldFileEntry_ReadsCorrectlyWhileASupersedingSnapshotIsReleased()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        builder.AddRow("kept.txt", root, RowFlags.InUse, 7, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);
        var oldSnapshot = Snapshot.Create([driveBlock]);
        var handle = FileEntry.Create(oldSnapshot, 0, 1);

        try
        {
            // Models FileIndex.PublishSnapshot: a new snapshot is created over the same block set
            // and the index's own reference to the previous one is released immediately, leaving
            // oldSnapshot as the only thing keeping driveBlock mapped for this held handle.
            await Snapshot.Create([driveBlock]).ReleaseNowAsync();

            Assert.AreEqual("kept.txt", handle.Name);
            Assert.AreEqual(7L, handle.Size);
        }
        finally
        {
            // A failed assertion would otherwise skip this and leave the block mapped through the
            // builder's own disposal, which is how one real failure turns into a cascade.
            await oldSnapshot.ReleaseNowAsync();
        }
    }


}
