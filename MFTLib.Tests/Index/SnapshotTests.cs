using System.Collections;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class SnapshotTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    sealed class ReleaseBeforeSecondBlockList(
        DriveBlock first,
        DriveBlock second,
        Action releaseBeforeSecond) : IReadOnlyList<DriveBlock>
    {
        public int Count => 2;

        public DriveBlock this[int index] => index switch
        {
            0 => first,
            1 => second,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public IEnumerator<DriveBlock> GetEnumerator()
        {
            yield return first;
            releaseBeforeSecond();
            yield return second;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    static DriveBlock OpenDriveBlock(SyntheticBlockBuilder builder, ushort ordinal)
    {
        var block = builder.OpenForReading(out _)!;
        return new DriveBlock(builder.DriveLetter, ordinal, block, deleteFileOnRelease: false);
    }

    static SyntheticBlockBuilder CompletedBuilder(char driveLetter)
    {
        var builder = new SyntheticBlockBuilder(driveLetter);
        builder.AddRoot();
        builder.Complete(ScanMoment);
        return builder;
    }

    [TestMethod]
    public void Create_TakesOneReferencePerDriveBlock()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);

        var snapshot = Snapshot.Create([driveBlock]);
        Assert.AreEqual(1, driveBlock.ReferenceCount);
        Assert.AreEqual(1, snapshot.DriveCount);

        snapshot.ReleaseNow();
        Assert.AreEqual(0, driveBlock.ReferenceCount);
        Assert.IsTrue(driveBlock.IsReleased);
    }

    [TestMethod]
    public void ReleaseNow_IsIdempotent()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);
        var snapshot = Snapshot.Create([driveBlock]);

        snapshot.ReleaseNow();
        snapshot.ReleaseNow();

        Assert.AreEqual(0, driveBlock.ReferenceCount);
    }

    [TestMethod]
    public void TwoSnapshotsOverOneBlock_KeepItMappedUntilBothRelease()
    {
        using var builder = CompletedBuilder('T');
        var driveBlock = OpenDriveBlock(builder, 0);

        var first = Snapshot.Create([driveBlock]);
        var second = Snapshot.Create([driveBlock]);
        Assert.AreEqual(2, driveBlock.ReferenceCount);

        first.ReleaseNow();
        Assert.IsFalse(driveBlock.IsReleased);

        second.ReleaseNow();
        Assert.IsTrue(driveBlock.IsReleased);
    }

    [TestMethod]
    public void GetDriveBlock_ResolvesByOrdinalAndByDriveLetter()
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
            snapshot.ReleaseNow();
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

    [TestMethod]
    public void Create_WhenPreviousOwnerReleasesBeforeLaterBlockFails_KeepsFirstBlockMapped()
    {
        using var firstBuilder = CompletedBuilder('T');
        using var secondBuilder = CompletedBuilder('U');
        var firstBlock = OpenDriveBlock(firstBuilder, 0);
        var secondBlock = OpenDriveBlock(secondBuilder, 1);
        var previousOwner = Snapshot.Create([firstBlock]);

        Assert.IsTrue(secondBlock.TryAddReference());
        secondBlock.Release();

        try
        {
            var blocks = new ReleaseBeforeSecondBlockList(
                firstBlock,
                secondBlock,
                previousOwner.ReleaseNow);

            Assert.ThrowsException<InvalidOperationException>(() => Snapshot.Create(blocks));
            Assert.AreEqual(1, firstBlock.ReferenceCount);
            Assert.IsFalse(firstBlock.IsReleased);
            Assert.AreEqual(256, firstBlock.Block.Rows.Length);
            Assert.AreEqual(1u, firstBlock.Block.Header.RowCount);
        }
        finally
        {
            if (firstBlock.ReferenceCount != 0)
            {
                firstBlock.Release();
            }
        }
    }
}
