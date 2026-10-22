using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A borrow is the reader gate a query holds for its whole duration. It is handed out only
///     while every borrowed block is still mapped, and a release waits for every outstanding
///     borrow before it unmaps, so a scan in flight never reads a freed mapping.
/// </summary>
[TestClass]
public class SnapshotBorrowTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    static DriveBlock OpenDriveBlock(SyntheticBlockBuilder builder)
    {
        return new DriveBlock(builder.Drive, 0, builder.OpenForReading(out _)!);
    }

    [TestMethod]
    public async Task Borrow_OnALiveSnapshot_IsHandedOut()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        DriveBlock[] snapshot = [OpenDriveBlock(builder)];
        try
        {
            using var borrow = new SnapshotBorrow(snapshot);

            Assert.IsFalse(snapshot[0].IsReleased);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_WhenABlockIsReleased_ThrowsAndReturnsTheBorrowsItTook()
    {
        using var liveBuilder = CompletedBlocks.Create(ScanMoment);
        using var releasedBuilder = CompletedBlocks.Create(ScanMoment, 'U');
        DriveBlock[] snapshot = [OpenDriveBlock(liveBuilder), OpenDriveBlock(releasedBuilder)];
        await snapshot[1].ReleaseState.ReleaseAsync();
        try
        {
            var thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(snapshot));

            Assert.Contains(nameof(DriveBlock), thrown.ObjectName);
            Assert.AreEqual(0, snapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_RejectedDuringRelease_CollectionDoesNotReturnAnotherReadersBorrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        DriveBlock[] snapshot = [driveBlock];
        var borrow = new SnapshotBorrow(snapshot);
        try
        {
            var release = snapshot.ReleaseNowAsync();
            Assert.IsTrue(snapshot[0].IsReleased);
            RejectABorrow(snapshot);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(1, snapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
            Assert.IsFalse(release.IsCompleted);
            Assert.IsFalse(driveBlock.ReleaseState.IsReleaseComplete);

            borrow.Dispose();
            await release;
            Assert.AreEqual(0, snapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
            Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await snapshot.ReleaseNowAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RejectABorrow(DriveBlock[] snapshot)
    {
        Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(snapshot));
    }

    [TestMethod]
    public async Task ReleaseNowAsync_WhileABorrowIsHeld_LeavesTheBlockMappedUntilItIsReturned()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        DriveBlock[] snapshot = [driveBlock];
        var borrow = new SnapshotBorrow(snapshot);
        try
        {
            var release = snapshot.ReleaseNowAsync();
            Assert.IsTrue(snapshot[0].IsReleased,
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
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ReleaseAsync_WhileABorrowIsHeld_CompletesOnlyOnceItIsReturned()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        DriveBlock[] snapshot = [driveBlock];
        var borrow = new SnapshotBorrow(snapshot);
        try
        {
            var release = snapshot[0].ReleaseState.ReleaseAsync().AsTask();

            Assert.IsFalse(release.IsCompleted, "the release completed with a borrow outstanding");
            Assert.IsFalse(driveBlock.ReleaseState.IsReleaseComplete);

            borrow.Dispose();
            Assert.IsTrue(await release);

            Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
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
        using var builder = CompletedBlocks.Create(ScanMoment);
        var driveBlock = OpenDriveBlock(builder);
        DriveBlock[] snapshot = [driveBlock];
        try
        {
            var first = new SnapshotBorrow(snapshot);
            using var second = new SnapshotBorrow(snapshot);
            first.Dispose();
            first.Dispose();

            Assert.AreEqual(1, snapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     A borrow whose owner was abandoned without disposing it is returned by its
    ///     finalizer, so a deterministic release is not held open for the rest of the
    ///     process by a reader that no longer exists. The double collect mirrors the repo's
    ///     other finalizer tests.
    /// </summary>
    [TestMethod]
    public async Task Borrow_AbandonedWithoutDisposal_IsReturnedWhenCollected()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        DriveBlock[] snapshot = [OpenDriveBlock(builder)];
        try
        {
            TakeAndAbandonABorrow(snapshot);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(0, snapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     No-inlined so the abandoned borrow is not kept reachable by this frame. The count is
    ///     asserted while the borrow is still held, so no collection can return it first.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TakeAndAbandonABorrow(DriveBlock[] snapshot)
    {
        // aislop-ignore-next-line dotnet/IDISP001 -- the abandoned, undisposed borrow is the subject: its finalizer must return it
        var borrow = new SnapshotBorrow(snapshot);
        Assert.AreEqual(1, snapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        GC.KeepAlive(borrow);
    }
}
