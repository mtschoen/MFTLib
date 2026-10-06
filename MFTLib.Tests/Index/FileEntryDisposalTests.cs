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
    public async Task Cleanup()
    {
        await _snapshot.ReleaseNowAsync();
        _builder.Dispose();
    }

    [TestMethod]
    public void IsDisposed_IsFalseWhileTheSnapshotIsLive()
    {
        Assert.IsFalse(_entry.IsDisposed);
        Assert.IsTrue(_entry.IsValid);
    }

    [TestMethod]
    public async Task IsDisposed_IsTrueOnceTheSnapshotIsReleased()
    {
        await _snapshot.ReleaseNowAsync();

        Assert.IsTrue(_entry.IsDisposed);
        Assert.IsTrue(_entry.IsValid, "IsValid answers whether the handle references a snapshot, not whether it is live");
    }

    [TestMethod]
    public void ToString_OnALiveEntry_NamesThePathAndTheRowKey()
    {
        var text = _entry.ToString();

        StringAssert.Contains(text, "readme.md");
        StringAssert.Contains(text, _entry.Id.ToString());
    }

    [TestMethod]
    public async Task ToString_OnAnEntryPastTheMaximumPathDepth_FallsBackToTheNameAndKey()
    {
        using var builder = new SyntheticBlockBuilder();
        var parent = builder.AddRoot();
        for (var depth = 0; depth <= BlockLayout.MaximumPathDepth; depth++)
        {
            parent = builder.AddRow("d" + depth, parent, RowFlags.InUse | RowFlags.Directory, 0, Moment,
                sequenceNumber: 0);
        }

        var deepest = builder.AddRow("deep.txt", parent, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, builder.OpenForReading(out _)!,
            rootDirectoryPath: TestDriveRoot.For('T'))]);
        try
        {
            var entry = FileEntry.Create(snapshot, 0, deepest);
            Assert.ThrowsException<InvalidDataException>(() => entry.Path);

            Assert.AreEqual($"deep.txt ({entry.Id})", entry.ToString());
            StringAssert.Contains(new SearchQuery("x", Under: entry).ToString(), "deep.txt");
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task ToString_OnAnEntryWhoseDriveHasNoRootDirectory_FallsBackToTheNameAndKey()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        var file = builder.AddRow("rootless.txt", root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);
        var snapshot = Snapshot.Create([new DriveBlock('T', 0, builder.OpenForReading(out _)!)]);
        try
        {
            var entry = FileEntry.Create(snapshot, 0, file);
            Assert.ThrowsException<InvalidOperationException>(() => entry.Path);

            Assert.AreEqual($"rootless.txt ({entry.Id})", entry.ToString());
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void ToString_OnTheDefaultValue_DoesNotThrow()
    {
        Assert.AreEqual("<invalid FileEntry>", default(FileEntry).ToString());
    }

    [TestMethod]
    public async Task ToString_AfterTheSnapshotIsReleased_DoesNotThrow()
    {
        await _snapshot.ReleaseNowAsync();

        Assert.AreEqual("<disposed FileEntry>", _entry.ToString());
    }

    [TestMethod]
    public void SearchQueryToString_WithADefaultUnder_DoesNotThrow()
    {
        var query = new SearchQuery("readme", Under: default(FileEntry));

        StringAssert.Contains(query.ToString(), "<invalid FileEntry>");
    }

    [TestMethod]
    public void IsDisposed_IsFalseForTheDefaultValue()
    {
        var defaultEntry = default(FileEntry);

        Assert.IsFalse(defaultEntry.IsValid);
        Assert.IsFalse(defaultEntry.IsDisposed);
    }

    [TestMethod]
    public async Task EveryDocumentedRead_ThrowsOnceTheSnapshotIsReleased()
    {
        await _snapshot.ReleaseNowAsync();
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
}
