using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The handle contract after a snapshot is released: every documented read throws
///     <see cref="ObjectDisposedException" />, <see cref="FileEntry.IsDisposed" /> reports it,
///     and <see cref="FileEntry.IsValid" /> keeps answering its own separate question.
/// </summary>
[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "The [TestCleanup] method releases the snapshot and disposes the builder, the " +
                     "MSTest-idiomatic disposal path this test project uses throughout; adding " +
                     "IDisposable to the test class itself would duplicate that lifecycle.")]
public class FileEntryDisposalTests
{
    static readonly DateTime Moment = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;
    FileEntry _entry;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder();
        var root = _builder.AddRoot();
        var documents = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment,
            sequenceNumber: 0);
        var readme = _builder.AddRow("readme.md", documents, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        _builder.Complete(Moment);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block, rootDirectoryPath: TestDriveRoot.For('T'))]);
        _entry = FileEntry.Create(_snapshot, 0, readme);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
        _builder.Dispose();
    }

    [TestMethod]
    public void IsDisposed_IsFalseWhileTheSnapshotIsLive()
    {
        Assert.IsFalse(_entry.IsDisposed);
        Assert.IsTrue(_entry.IsValid);
    }

    [TestMethod]
    public void IsDisposed_IsTrueOnceTheSnapshotIsReleased()
    {
        _snapshot.ReleaseNow();

        Assert.IsTrue(_entry.IsDisposed);
        Assert.IsTrue(_entry.IsValid, "IsValid answers whether the handle references a snapshot, not whether it is live");
    }

    [TestMethod]
    public void IsDisposed_IsFalseForTheDefaultValue()
    {
        var defaultEntry = default(FileEntry);

        Assert.IsFalse(defaultEntry.IsValid);
        Assert.IsFalse(defaultEntry.IsDisposed);
    }

    [TestMethod]
    public void EveryDocumentedRead_ThrowsOnceTheSnapshotIsReleased()
    {
        _snapshot.ReleaseNow();
        var entry = _entry;

        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Name);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Size);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.SizeKnown);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Modified);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Attributes);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.IsDirectory);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.IsDeleted);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Id);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Path);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = entry.Parent);
        Assert.ThrowsException<ObjectDisposedException>(() => entry.Children());
        Assert.ThrowsException<ObjectDisposedException>(() => entry.Open(FileAccess.Read));
    }

    [TestMethod]
    public void ToString_DoesNotThrowOnADisposedHandle()
    {
        _snapshot.ReleaseNow();

        Assert.AreEqual("<disposed FileEntry>", _entry.ToString());
        Assert.AreEqual("<invalid FileEntry>", default(FileEntry).ToString());
    }
}
