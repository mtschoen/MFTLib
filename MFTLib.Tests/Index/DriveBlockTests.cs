using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class DriveBlockTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    static SyntheticBlockBuilder CompletedBuilder()
    {
        var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        builder.Complete(ScanMoment);
        return builder;
    }

    [TestMethod]
    public void NewDriveBlock_HasNoReferencesAndIsNotReleased()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        Assert.AreEqual(0, driveBlock.ReferenceCount);
        Assert.IsFalse(driveBlock.IsReleased);
        Assert.AreEqual('T', driveBlock.DriveLetter);
        Assert.AreEqual(ProducerKind.Enumeration, driveBlock.ProducerKind);

        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();
    }

    [TestMethod]
    public void LastRelease_DisposesTheBlock()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        Assert.IsTrue(driveBlock.TryAddReference());
        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();
        Assert.IsFalse(driveBlock.IsReleased);

        driveBlock.Release();
        Assert.IsTrue(driveBlock.IsReleased);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = block.Rows.Length);
    }

    [TestMethod]
    public void ScheduleDeleteAt_DeletesTheOverridePathInsteadOfTheBlocksOwnPath()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ScheduleDeleteAt(renamedPath);

        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();

        Assert.IsFalse(File.Exists(renamedPath));
    }

    [TestMethod]
    public void Release_WithOverridePathLocked_DoesNotThrow()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ScheduleDeleteAt(renamedPath);

        // ReadWrite share matches the block's own still-open handle so this second handle can
        // coexist with it; omitting FileShare.Delete is what makes Windows' delete inside
        // Release hit a sharing violation, swallowed by Release. Unix has no mandatory
        // share-mode locking, so the delete there succeeds even with the handle still open.
        // Either way Release must not throw.
        using var lockingHandle =
            new FileStream(renamedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.IsTrue(driveBlock.TryAddReference());

        driveBlock.Release();
    }

    [TestMethod]
    public void Release_WithOverridePathReadOnly_DoesNotThrow()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        File.SetAttributes(renamedPath, FileAttributes.ReadOnly);
        driveBlock.ScheduleDeleteAt(renamedPath);

        try
        {
            Assert.IsTrue(driveBlock.TryAddReference());

            // Windows treats the read-only attribute as delete-denying, swallowed by Release.
            // Unix unlink ignores a file's own permission bits, so the delete there succeeds.
            // Either way Release must not throw.
            driveBlock.Release();
        }
        finally
        {
            if (File.Exists(renamedPath))
            {
                File.SetAttributes(renamedPath, FileAttributes.Normal);
            }
        }
    }

    [TestMethod]
    public void TryAddReference_AfterRelease_ReturnsFalse()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();

        Assert.IsFalse(driveBlock.TryAddReference());
        Assert.AreEqual(0, driveBlock.ReferenceCount);
    }

    [TestMethod]
    public void Release_BelowZero_Throws()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block);

        Assert.ThrowsException<InvalidOperationException>(driveBlock.Release);
    }
}
