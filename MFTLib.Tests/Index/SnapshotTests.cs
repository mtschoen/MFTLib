using System.Runtime.CompilerServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class SnapshotTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    static DriveBlock OpenDriveBlock(SyntheticBlockBuilder builder, ushort ordinal)
    {
        var block = builder.OpenForReading(out _)!;
        return new DriveBlock(builder.DriveLetter, ordinal, block);
    }

    static SyntheticBlockBuilder CompletedBuilder(char driveLetter)
    {
        var builder = new SyntheticBlockBuilder(driveLetter);
        builder.AddRoot();
        builder.Complete(ScanMoment);
        return builder;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference<Snapshot> CreateUnrootedSnapshot(DriveBlock driveBlock)
    {
        var snapshot = Snapshot.Create([driveBlock]);
        return new WeakReference<Snapshot>(snapshot);
    }

    [TestMethod]
    public async Task Finalizer_ReleasesDriveBlockWhenSnapshotBecomesUnreachable()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);
        var weakSnapshot = CreateUnrootedSnapshot(driveBlock);
        Assert.AreEqual(1, driveBlock.ReferenceCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.IsFalse(weakSnapshot.TryGetTarget(out _));
        Assert.AreEqual(0, driveBlock.ReferenceCount);
        Assert.IsTrue(driveBlock.IsReleased);
    }

    [TestMethod]
    public async Task Create_TakesOneReferencePerDriveBlock()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);

        var snapshot = Snapshot.Create([driveBlock]);
        try
        {
            Assert.AreEqual(1, driveBlock.ReferenceCount);
            Assert.AreEqual(1, snapshot.DriveCount);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }

        Assert.AreEqual(0, driveBlock.ReferenceCount);
        Assert.IsTrue(driveBlock.IsReleased);
    }

    [TestMethod]
    public async Task ReleaseNow_IsIdempotent()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);
        var snapshot = Snapshot.Create([driveBlock]);

        try
        {
            await snapshot.ReleaseNowAsync();
            await snapshot.ReleaseNowAsync();
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }

        Assert.AreEqual(0, driveBlock.ReferenceCount);
    }

    [TestMethod]
    public async Task TwoSnapshotsOverOneBlock_KeepItMappedUntilBothRelease()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);

        var first = Snapshot.Create([driveBlock]);
        var second = Snapshot.Create([driveBlock]);
        try
        {
            Assert.AreEqual(2, driveBlock.ReferenceCount);

            await first.ReleaseNowAsync();
            Assert.IsFalse(driveBlock.IsReleased);

            await second.ReleaseNowAsync();
            Assert.IsTrue(driveBlock.IsReleased);
        }
        finally
        {
            await first.ReleaseNowAsync();
            await second.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task GetDriveBlock_ResolvesByOrdinalAndByDriveLetter()
    {
        using var firstBuilder = CompletedBuilder('T');
        using var secondBuilder = CompletedBuilder('U');
        var first = OpenDriveBlock(firstBuilder, 0);
        var second = OpenDriveBlock(secondBuilder, 1);

        var snapshot = Snapshot.Create([first, second]);
        try
        {
            Assert.AreSame(first, snapshot.GetDriveBlock(0));
            Assert.AreSame(second, snapshot.GetDriveBlock(1));
            Assert.AreSame(second, snapshot.FindDriveBlock('U'));
            Assert.IsNull(snapshot.FindDriveBlock('Z'));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Create_WithAnAlreadyReleasedBlock_Throws()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);
        Assert.IsTrue(driveBlock.TryAddReference());
        driveBlock.Release();

        Assert.ThrowsException<InvalidOperationException>(() => Snapshot.Create([driveBlock]));
    }
}
