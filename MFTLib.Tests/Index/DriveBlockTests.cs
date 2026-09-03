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
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false);

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
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false);

        Assert.IsTrue(driveBlock.TryAddReference());
        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();
        Assert.IsFalse(driveBlock.IsReleased);

        driveBlock.Release();
        Assert.IsTrue(driveBlock.IsReleased);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = block.Rows.Length);
    }

    [TestMethod]
    public void DeleteFileOnRelease_RemovesTheSupersededBlockFile()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: true);

        Assert.IsTrue(driveBlock.TryAddReference());
        Assert.IsTrue(File.Exists(builder.BlockPath));

        driveBlock.Release();
        Assert.IsFalse(File.Exists(builder.BlockPath));
    }

    [TestMethod]
    public void ScheduleDeleteAt_DeletesTheOverridePathInsteadOfTheBlocksOwnPath()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false);

        var renamedPath = builder.BlockPath + ".retired-1";
        File.Move(builder.BlockPath, renamedPath);
        driveBlock.ScheduleDeleteAt(renamedPath);

        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();

        Assert.IsFalse(File.Exists(renamedPath));
    }

    [TestMethod]
    public void TryAddReference_AfterRelease_ReturnsFalse()
    {
        using var builder = CompletedBuilder();
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false);

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
        var driveBlock = new DriveBlock('T', 0, block, deleteFileOnRelease: false);

        Assert.ThrowsException<InvalidOperationException>(driveBlock.Release);
    }
}
