using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using MFTLib.Tests.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class DirectLargestFilesTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    const int LargeRowCount = 100_000;

    SyntheticBlockBuilder _builder = null!;
    FileIndex _index = null!;
    uint _documentsRow;

    [TestInitialize]
    public async Task Initialize()
    {
        _builder = new SyntheticBlockBuilder(slotCapacity: 512, namePoolCapacity: 8192);
        var root = _builder.AddRoot(_builder.DirectoryPath);
        _documentsRow = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        var pictures = _builder.AddRow("Pictures", root, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        _builder.AddRow("huge.bin", _documentsRow, RowFlags.InUse, 9_000_000, Moment, sequenceNumber: 0);
        _builder.AddRow("large.bin", _documentsRow, RowFlags.InUse, 5_000_000, Moment, sequenceNumber: 0);
        _builder.AddRow("medium.bin", pictures, RowFlags.InUse, 3_000_000, Moment, sequenceNumber: 0);
        _builder.AddRow("small.bin", pictures, RowFlags.InUse, 1_000, Moment, sequenceNumber: 0);
        _builder.AddRow("readme.md", _documentsRow, RowFlags.InUse, 10, Moment, sequenceNumber: 0);
        _builder.AddRow("readme.md", pictures, RowFlags.InUse, 20, Moment, sequenceNumber: 0);
        _builder.AddRow("gone.bin", _documentsRow, RowFlags.InUse | RowFlags.Tombstone, 99_000_000, Moment, sequenceNumber: 0);
        _builder.Complete(Moment);
        _index = await OpenIndexAsync(_builder);
    }

    static async Task<FileIndex> OpenIndexAsync(SyntheticBlockBuilder builder)
    {
        var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive(builder.DriveLetter, builder.DirectoryPath, builder.VolumeSerial)],
            CacheDirectory = builder.DirectoryPath,
            InitialOpenCacheOnly = true,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State, index.Drives.Single().FailureMessage);
        return index;
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _index.DisposeAsync();
        _builder.Dispose();
    }

    [TestMethod]
    public void Largest_ReturnsTheBiggestFilesInDescendingOrder()
    {
        var results = LargestFiles.Find(_index, 3, under: null, CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { "huge.bin", "large.bin", "medium.bin" },
            results.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void Largest_ExcludesDirectoriesAndTombstones()
    {
        var results = LargestFiles.Find(_index, 10, under: null, CancellationToken.None);
        Assert.IsFalse(results.Any(entry => entry.IsDirectory));
        Assert.IsFalse(results.Any(entry => entry.Name == "gone.bin"));
    }

    [TestMethod]
    public async Task Largest_ExcludesSizeUnknownRows()
    {
        using var builder = new SyntheticBlockBuilder('K');
        var root = builder.AddRoot(builder.DirectoryPath);
        builder.AddRow("known-small.txt", root, RowFlags.InUse, 10, Moment, sequenceNumber: 0);
        builder.AddRow("unknown-size.txt", root, RowFlags.InUse | RowFlags.SizeUnknown, 0, Moment, sequenceNumber: 0);
        builder.AddRow("known-big.txt", root, RowFlags.InUse, 1000, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        await using var index = await OpenIndexAsync(builder);
        var results = LargestFiles.Find(index, 10, under: null, CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { "known-big.txt", "known-small.txt" },
            results.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void Largest_RespectsTheSubtreeRestriction()
    {
        var documents = FileEntry.Create(_index.CurrentSnapshot, 0, _documentsRow);
        var results = LargestFiles.Find(_index, 5, documents, CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { "huge.bin", "large.bin", "readme.md" },
            results.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void Largest_SubtreeIncludesTheAncestorFile()
    {
        var file = _index.Search(new SearchQuery("huge.bin", NameMatchMode.Exact)).Single();
        var results = LargestFiles.Find(_index, 5, file, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { file.RecordKey }, results.Select(entry => entry.RecordKey).ToArray());
    }

    [TestMethod]
    public void Largest_CountLargerThanTheDriveReturnsEverything()
    {
        var results = LargestFiles.Find(_index, 1000, under: null, CancellationToken.None);
        Assert.AreEqual(6, results.Count);
    }

    [TestMethod]
    public void Largest_MaximumCountOverSmallIndex_ReturnsOnlyTheAvailableFiles()
    {
        var results = LargestFiles.Find(_index, int.MaxValue, under: null, CancellationToken.None);
        CollectionAssert.AreEqual(new long[] { 9_000_000, 5_000_000, 3_000_000, 1_000, 20, 10 },
            results.Select(entry => entry.Size).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void Largest_CancellationDuringResultAssembly_StopsBeforeMoreWorkOrReturn(int cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Action cancel = cancellation.Cancel;
        var assembled = 0;
        Assert.ThrowsException<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 3, under: null, token, completed =>
            {
                assembled = completed;
                if (completed == cancelAfter)
                {
                    cancel();
                }
            }));
        Assert.AreEqual(cancelAfter, assembled);
        Assert.AreEqual(0, _index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    [TestMethod]
    public void Largest_ZeroCountReturnsEmpty()
    {
        Assert.AreEqual(0, LargestFiles.Find(_index, 0, under: null, CancellationToken.None).Count);
    }

    [TestMethod]
    public void Largest_NegativeCountThrows()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LargestFiles.Find(_index, -1, under: null, CancellationToken.None));
    }

    [TestMethod]
    public void Largest_WithCancelledToken_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 10, under: null, token));
    }

    [TestMethod]
    public void Largest_Under_WithCancelledToken_ThrowsOperationCanceled()
    {
        var documents = FileEntry.Create(_index.CurrentSnapshot, 0, _documentsRow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 10, under: documents, token));
    }

    [TestMethod]
    public void Largest_Under_InvalidAncestor_ReturnsEmpty()
    {
        var invalidAncestor = default(FileEntry);
        var results = LargestFiles.Find(_index, 5, invalidAncestor, CancellationToken.None);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Largest_Under_InvalidAncestor_WithCancelledToken_ThrowsOperationCanceled()
    {
        var invalidAncestor = default(FileEntry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 10, under: invalidAncestor, token));
    }

    [TestMethod]
    public async Task Largest_OverALargeBlockReturnsTheExactTopFiveInDescendingOrder()
    {
        using var builder = new SyntheticBlockBuilder('L',
            slotCapacity: LargeRowCount + 10, namePoolCapacity: (uint)LargeRowCount * 40);
        var root = builder.AddRoot(builder.DirectoryPath);
        for (var size = 1; size <= LargeRowCount; size++)
        {
            builder.AddRow($"file{size}.bin", root, RowFlags.InUse, size, Moment, sequenceNumber: 0);
        }

        builder.Complete(Moment);
        await using var index = await OpenIndexAsync(builder);
        var results = LargestFiles.Find(index, 5, under: null, CancellationToken.None);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 5).Select(offset => $"file{LargeRowCount - offset}.bin").ToArray(),
            results.Select(entry => entry.Name).ToArray());
    }
}
