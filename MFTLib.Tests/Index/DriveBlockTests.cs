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
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);

        Assert.IsFalse(driveBlock.IsReleased);
        Assert.AreEqual(new VolumeKey('T'), driveBlock.Drive);
        Assert.AreEqual(ProducerKind.Enumeration, driveBlock.ProducerKind);

        Assert.IsTrue(driveBlock.ReleaseState.ReleaseUnborrowed());
    }

    [TestMethod]
    public void Release_DisposesTheBlockOnce()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);

        Assert.IsTrue(driveBlock.ReleaseState.ReleaseUnborrowed());
        Assert.IsFalse(driveBlock.ReleaseState.ReleaseUnborrowed());

        Assert.IsTrue(driveBlock.IsReleased);
        Assert.IsTrue(driveBlock.ReleaseState.IsReleaseComplete);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = block.Rows.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static DriveBlockRelease CreateUnrootedDriveBlock(BlockFile block, Action? releaseStarted = null)
    {
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);
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

        Assert.IsTrue(release.IsReleaseStarted);
        Assert.IsGreaterThan(0, block.Rows.Length, "the block is still mapped");
        block.Dispose();
    }

    [TestMethod]
    public void ScheduleDeleteAt_DeletesTheOverridePathInsteadOfTheBlocksOwnPath()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        driveBlock.ReleaseState.ReleaseUnborrowed();

        Assert.IsFalse(File.Exists(renamedPath));
    }

    [TestMethod]
    public void Release_WithOverridePathLocked_DoesNotThrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        using var lockingHandle =
            new FileStream(renamedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        driveBlock.ReleaseState.ReleaseUnborrowed();
    }

    [TestMethod]
    public void Release_WithOverridePathReadOnly_DoesNotThrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        File.SetAttributes(renamedPath, FileAttributes.ReadOnly);
        driveBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        try
        {
            driveBlock.ReleaseState.ReleaseUnborrowed();
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
