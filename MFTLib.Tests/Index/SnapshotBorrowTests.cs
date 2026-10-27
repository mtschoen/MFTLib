using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A borrow is the reader gate a query holds for its whole duration. It is handed out only
///     while the borrowed block is still mapped, and a release waits for every outstanding
///     borrow before it unmaps, so a scan in flight never reads a freed mapping.
/// </summary>
[TestClass]
public class SnapshotBorrowTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    static DriveBlock OpenDriveBlock(SyntheticBlockBuilder builder)
    {
        return new DriveBlock(new VolumeKey(builder.Drive), builder.OpenForReading(out _)!);
    }

    [TestMethod]
    public async Task Borrow_OnALiveBlock_IsHandedOut()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        try
        {
            using var borrow = new SnapshotBorrow(driveBlock);

            Assert.IsFalse(driveBlock.IsReleased);
        }
        finally
        {
            await driveBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_WhenBlockIsReleased_Throws()
    {
        using var releasedBuilder = CompletedBlocks.Create(ScanMoment, 'U');
        var releasedBlock = OpenDriveBlock(releasedBuilder);
        await releasedBlock.ReleaseState.DrainAndReleaseAsync();

        var thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(releasedBlock));

        Assert.Contains(nameof(DriveBlock), thrown.ObjectName);
        Assert.AreEqual(0, releasedBlock.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public async Task Borrow_RejectedDuringRelease_CollectionDoesNotReturnAnotherReadersBorrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        var borrow = new SnapshotBorrow(driveBlock);
        try
        {
            var release = driveBlock.ReleaseNowAsync();
            Assert.IsTrue(driveBlock.IsReleased);
            RejectABorrow(driveBlock);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(1, driveBlock.ReleaseState.Hooks.OutstandingBorrowCount);
            Assert.IsFalse(release.IsCompleted);
            Assert.IsFalse(driveBlock.ReleaseState.IsReleaseComplete);

            borrow.Dispose();
            await release;
            Assert.AreEqual(0, driveBlock.ReleaseState.Hooks.OutstandingBorrowCount);
            Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await driveBlock.ReleaseNowAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RejectABorrow(DriveBlock driveBlock)
    {
        Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(driveBlock));
    }

    [TestMethod]
    public async Task ReleaseNowAsync_WhileABorrowIsHeld_LeavesTheBlockMappedUntilItIsReturned()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        var borrow = new SnapshotBorrow(driveBlock);
        try
        {
            var release = driveBlock.ReleaseNowAsync();
            Assert.IsTrue(driveBlock.IsReleased,
                "the release never began, so the borrow was never what held it up");

            Assert.IsFalse(driveBlock.ReleaseState.IsReleaseComplete,
                "the block was unmapped while a borrow was outstanding");
            Assert.IsFalse(release.IsCompleted);

            borrow.Dispose();
            await release;

            Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await driveBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ReleaseAsync_WhileABorrowIsHeld_CompletesOnlyOnceItIsReturned()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        var borrow = new SnapshotBorrow(driveBlock);
        try
        {
            var release = driveBlock.ReleaseState.DrainAndReleaseAsync();

            Assert.IsFalse(release.IsCompleted, "the release completed with a borrow outstanding");
            Assert.IsFalse(driveBlock.ReleaseState.IsReleaseComplete);

            borrow.Dispose();
            Assert.IsTrue(await release);

            Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await driveBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_ReturnedTwice_CountsOnce()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        try
        {
            var first = new SnapshotBorrow(driveBlock);
            using var second = new SnapshotBorrow(driveBlock);
            first.Dispose();
            first.Dispose();

            Assert.AreEqual(1, driveBlock.ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await driveBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_AbandonedWithoutDisposal_IsReturnedWhenCollected()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        try
        {
            TakeAndAbandonABorrow(driveBlock);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(0, driveBlock.ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await driveBlock.ReleaseNowAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TakeAndAbandonABorrow(DriveBlock driveBlock)
    {
        // aislop-ignore-next-line dotnet/IDISP001 -- the abandoned, undisposed borrow is the subject: its finalizer must return it
        var borrow = new SnapshotBorrow(driveBlock);
        Assert.AreEqual(1, driveBlock.ReleaseState.Hooks.OutstandingBorrowCount);
        GC.KeepAlive(borrow);
    }
}
