using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class RootBlockTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void NewRootBlock_IsNotReleased()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var rootBlock = new RootBlock(new VolumeKey('T'), block);

        Assert.IsFalse(rootBlock.IsReleased);
        Assert.AreEqual(new VolumeKey('T'), rootBlock.Root);
        Assert.AreEqual(ProducerKind.Enumeration, rootBlock.ProducerKind);

        Assert.IsTrue(rootBlock.ReleaseState.ReleaseUnborrowed());
    }

    [TestMethod]
    public void Release_DisposesTheBlockOnce()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var rootBlock = new RootBlock(new VolumeKey('T'), block);

        Assert.IsTrue(rootBlock.ReleaseState.ReleaseUnborrowed());
        Assert.IsFalse(rootBlock.ReleaseState.ReleaseUnborrowed());

        Assert.IsTrue(rootBlock.IsReleased);
        Assert.IsTrue(rootBlock.ReleaseState.IsReleaseComplete);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = block.Rows.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static RootBlockRelease CreateUnrootedRootBlock(BlockFile block, Action? releaseStarted = null)
    {
        var rootBlock = new RootBlock(new VolumeKey('T'), block);
        rootBlock.ReleaseState.Hooks._releaseStarted = releaseStarted;
        return rootBlock.ReleaseState;
    }

    [TestMethod]
    public void Finalizer_ReleasesTheBlockWhenItBecomesUnreachable()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var release = CreateUnrootedRootBlock(block);

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
        var release = CreateUnrootedRootBlock(block,
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
        var rootBlock = new RootBlock(new VolumeKey('T'), block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        rootBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        rootBlock.ReleaseState.ReleaseUnborrowed();

        Assert.IsFalse(File.Exists(renamedPath));
    }

    [TestMethod]
    public void Release_WithOverridePathLocked_DoesNotThrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var rootBlock = new RootBlock(new VolumeKey('T'), block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        rootBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        using var lockingHandle =
            new FileStream(renamedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        rootBlock.ReleaseState.ReleaseUnborrowed();
    }

    [TestMethod]
    public void Release_WithOverridePathReadOnly_DoesNotThrow()
    {
        using var builder = CompletedBlocks.Create(ScanMoment);
        var block = builder.OpenForReading(out _)!;
        var rootBlock = new RootBlock(new VolumeKey('T'), block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        File.SetAttributes(renamedPath, FileAttributes.ReadOnly);
        rootBlock.ReleaseState.ScheduleDeleteAt(renamedPath);

        try
        {
            rootBlock.ReleaseState.ReleaseUnborrowed();
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
