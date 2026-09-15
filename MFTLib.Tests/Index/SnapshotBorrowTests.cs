using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A borrow is the reader gate a query holds for its whole duration. A snapshot hands one
///     out only while its blocks are still mapped, and a release waits for every outstanding
///     borrow before it unmaps, so a scan in flight never reads a freed mapping.
/// </summary>
[TestClass]
public class SnapshotBorrowTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    static SyntheticBlockBuilder CompletedBuilder(char driveLetter = 'T')
    {
        var builder = new SyntheticBlockBuilder(driveLetter);
        builder.AddRoot();
        builder.Complete(ScanMoment);
        return builder;
    }

    static DriveBlock OpenDriveBlock(SyntheticBlockBuilder builder)
    {
        return new DriveBlock(builder.DriveLetter, 0, builder.OpenForReading(out _)!);
    }

    [TestMethod]
    public async Task Borrow_OnALiveSnapshot_IsHandedOut()
    {
        using var builder = CompletedBuilder();
        var snapshot = Snapshot.Create([OpenDriveBlock(builder)]);
        try
        {
            using var borrow = snapshot.Borrow();

            Assert.IsFalse(snapshot.IsReleased);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_AfterRelease_Throws()
    {
        using var builder = CompletedBuilder();
        var snapshot = Snapshot.Create([OpenDriveBlock(builder)]);
        await snapshot.ReleaseNowAsync();

        var thrown = Assert.ThrowsException<ObjectDisposedException>(() => snapshot.Borrow());

        StringAssert.Contains(thrown.ObjectName, nameof(Snapshot));
    }

    [TestMethod]
    public async Task ReleaseNowAsync_WhileABorrowIsHeld_LeavesTheBlockMappedUntilItIsReturned()
    {
        using var builder = CompletedBuilder();
        var driveBlock = OpenDriveBlock(builder);
        var snapshot = Snapshot.Create([driveBlock]);
        var borrow = snapshot.Borrow();
        try
        {
            var release = snapshot.ReleaseNowAsync().AsTask();
            Assert.IsTrue(snapshot.IsReleased,
                "the release never began, so the borrow was never what held it up");

            Assert.IsFalse(driveBlock.IsReleased,
                "the block was unmapped while a borrow was outstanding");
            Assert.IsFalse(release.IsCompleted);

            borrow.Dispose();
            await release;

            Assert.IsTrue(driveBlock.IsReleased);
        }
        finally
        {
            borrow.Dispose();
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ReleaseAsync_WhileABorrowIsHeld_CompletesOnlyOnceItIsReturned()
    {
        using var builder = CompletedBuilder();
        var driveBlock = OpenDriveBlock(builder);
        var snapshot = Snapshot.Create([driveBlock]);
        var borrow = snapshot.Borrow();
        try
        {
            var release = snapshot.ReleaseState.ReleaseAsync().AsTask();

            Assert.IsFalse(release.IsCompleted, "the release completed with a borrow outstanding");
            Assert.IsFalse(driveBlock.IsReleased);

            borrow.Dispose();
            Assert.IsTrue(await release);

            Assert.IsTrue(driveBlock.IsReleased);
        }
        finally
        {
            borrow.Dispose();
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     Returning a borrow twice is a caller mistake that must not decrement the count past
    ///     the borrows that are actually outstanding, or a release would unmap under a reader
    ///     that is still scanning.
    /// </summary>
    [TestMethod]
    public async Task Borrow_ReturnedTwice_CountsOnce()
    {
        using var builder = CompletedBuilder();
        var driveBlock = OpenDriveBlock(builder);
        var snapshot = Snapshot.Create([driveBlock]);
        try
        {
            var first = snapshot.Borrow();
            using var second = snapshot.Borrow();
            first.Dispose();
            first.Dispose();

            Assert.AreEqual(1, snapshot.ReleaseState.OutstandingBorrowCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
