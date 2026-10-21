using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class DriveBlockTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void NewDriveBlock_IsNotReleased()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        Assert.IsFalse(driveBlock.IsReleased);
        Assert.AreEqual('T', driveBlock.Drive);
        Assert.AreEqual(ProducerKind.Enumeration, driveBlock.ProducerKind);

        Assert.IsTrue(driveBlock.ReleaseState.Release());
    }

    [TestMethod]
    public void Release_DisposesTheBlockOnce()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        Assert.IsTrue(driveBlock.ReleaseState.Release());
        Assert.IsFalse(driveBlock.ReleaseState.Release());

        Assert.IsTrue(driveBlock.IsReleased);
        Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = block.Rows.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static DriveBlockRelease CreateUnrootedDriveBlock(BlockFile block, Action? releaseStarted = null)
    {
        var driveBlock = new DriveBlock('T', 0, block);
        driveBlock.ReleaseState.Hooks._releaseStarted = releaseStarted;
        return driveBlock.ReleaseState;
    }

    [TestMethod]
    public void Finalizer_ReleasesTheBlockWhenItBecomesUnreachable()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var release = CreateUnrootedDriveBlock(block);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.IsTrue(release.IsReleaseComplete);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = block.Rows.Length);
    }

    [TestMethod]
    public void Finalizer_WhenTheReleaseThrows_SwallowsItAndLeavesTheBlockMapped()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var release = CreateUnrootedDriveBlock(block,
            () => throw new InvalidOperationException("Simulated release failure."));

        GC.Collect();
        GC.WaitForPendingFinalizers();

        // An exception escaping a finalizer terminates the process, so reaching this
        // assertion at all is half of the contract; the other half is that the failed
        // release ran before the block was unmapped.
        Assert.IsTrue(release.IsReleaseStarted);
        Assert.IsTrue(block.Rows.Length > 0, "the block is still mapped");
        block.Dispose();
    }

    [TestMethod]
    public void ScheduleDeleteAt_DeletesTheOverridePathInsteadOfTheBlocksOwnPath()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        driveBlock.ReleaseState.Release();

        Assert.IsFalse(File.Exists(renamedPath));
    }

    [TestMethod]
    public void Release_WithOverridePathLocked_DoesNotThrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        // ReadWrite share matches the block's own still-open handle so this second handle can
        // coexist with it; omitting FileShare.Delete is what makes Windows' delete inside
        // Release hit a sharing violation, swallowed by Release. Unix has no mandatory
        // share-mode locking, so the delete there succeeds even with the handle still open.
        // Either way Release must not throw.
        using var lockingHandle =
            new FileStream(renamedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        driveBlock.ReleaseState.Release();
    }

    [TestMethod]
    public void Release_WithOverridePathReadOnly_DoesNotThrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        File.SetAttributes(renamedPath, FileAttributes.ReadOnly);
        driveBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        try
        {
            // Windows treats the read-only attribute as delete-denying, swallowed by Release.
            // Unix unlink ignores a file's own permission bits, so the delete there succeeds.
            // Either way Release must not throw.
            driveBlock.ReleaseState.Release();
        }
        finally
        {
            if (File.Exists(renamedPath))
            {
                File.SetAttributes(renamedPath, FileAttributes.Normal);
            }
        }
    }
}
