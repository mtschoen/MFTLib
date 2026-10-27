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

    static RootBlock OpenRootBlock(SyntheticBlockBuilder builder)
    {
        return new RootBlock(new VolumeKey(builder.Drive), builder.OpenForReading(out _)!);
    }

    [TestMethod]
    public async Task Borrow_OnALiveBlock_IsHandedOut()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var rootBlock = OpenRootBlock(builder);
        try
        {
            using var borrow = new SnapshotBorrow(rootBlock);

            Assert.IsFalse(rootBlock.IsReleased);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_WhenBlockIsReleased_Throws()
    {
        using var releasedBuilder = CompletedBlocks.Create(ScanMoment, 'U');
        var releasedBlock = OpenRootBlock(releasedBuilder);
        await releasedBlock.ReleaseState.DrainAndReleaseAsync();

        var thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(releasedBlock));

        Assert.Contains(nameof(RootBlock), thrown.ObjectName);
        Assert.AreEqual(0, releasedBlock.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public async Task Borrow_RejectedDuringRelease_CollectionDoesNotReturnAnotherReadersBorrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var rootBlock = OpenRootBlock(builder);
        var borrow = new SnapshotBorrow(rootBlock);
        try
        {
            var release = rootBlock.ReleaseNowAsync();
            Assert.IsTrue(rootBlock.IsReleased);
            RejectABorrow(rootBlock);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(1, rootBlock.ReleaseState.Hooks.OutstandingBorrowCount);
            Assert.IsFalse(release.IsCompleted);
            Assert.IsFalse(rootBlock.ReleaseState.IsReleaseComplete);

            borrow.Dispose();
            await release;
            Assert.AreEqual(0, rootBlock.ReleaseState.Hooks.OutstandingBorrowCount);
            Assert.IsTrue(rootBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await rootBlock.ReleaseNowAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RejectABorrow(RootBlock rootBlock)
    {
        Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(rootBlock));
    }

    [TestMethod]
    public async Task ReleaseNowAsync_WhileABorrowIsHeld_LeavesTheBlockMappedUntilItIsReturned()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var rootBlock = OpenRootBlock(builder);
        var borrow = new SnapshotBorrow(rootBlock);
        try
        {
            var release = rootBlock.ReleaseNowAsync();
            Assert.IsTrue(rootBlock.IsReleased,
                "the release never began, so the borrow was never what held it up");

            Assert.IsFalse(rootBlock.ReleaseState.IsReleaseComplete,
                "the block was unmapped while a borrow was outstanding");
            Assert.IsFalse(release.IsCompleted);

            borrow.Dispose();
            await release;

            Assert.IsTrue(rootBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ReleaseAsync_WhileABorrowIsHeld_CompletesOnlyOnceItIsReturned()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var rootBlock = OpenRootBlock(builder);
        var borrow = new SnapshotBorrow(rootBlock);
        try
        {
            var release = rootBlock.ReleaseState.DrainAndReleaseAsync();

            Assert.IsFalse(release.IsCompleted, "the release completed with a borrow outstanding");
            Assert.IsFalse(rootBlock.ReleaseState.IsReleaseComplete);

            borrow.Dispose();
            Assert.IsTrue(await release);

            Assert.IsTrue(rootBlock.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            borrow.Dispose();
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_ReturnedTwice_CountsOnce()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var rootBlock = OpenRootBlock(builder);
        try
        {
            var first = new SnapshotBorrow(rootBlock);
            using var second = new SnapshotBorrow(rootBlock);
            first.Dispose();
            first.Dispose();

            Assert.AreEqual(1, rootBlock.ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Borrow_AbandonedWithoutDisposal_IsReturnedWhenCollected()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var rootBlock = OpenRootBlock(builder);
        try
        {
            TakeAndAbandonABorrow(rootBlock);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.AreEqual(0, rootBlock.ReleaseState.Hooks.OutstandingBorrowCount);
        }
        finally
        {
            await rootBlock.ReleaseNowAsync();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void TakeAndAbandonABorrow(RootBlock rootBlock)
    {
        // aislop-ignore-next-line dotnet/IDISP001 -- the abandoned, undisposed borrow is the subject: its finalizer must return it
        var borrow = new SnapshotBorrow(rootBlock);
        Assert.AreEqual(1, rootBlock.ReleaseState.Hooks.OutstandingBorrowCount);
        GC.KeepAlive(borrow);
    }
}
