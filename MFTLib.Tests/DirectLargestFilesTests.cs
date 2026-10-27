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
            Root = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey(builder.Drive), builder.DirectoryPath, builder.VolumeSerial, ProducerKind.Enumeration),
            CacheDirectory = builder.DirectoryPath,
            CacheMode = CacheMode.ExistingOnly
        }, CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, index.Status.State, index.Status.Failure?.Message);
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
        Assert.AreSequenceEqual(
            ["huge.bin", "large.bin", "medium.bin"], [.. results.Select(entry => entry.Name)]);
    }

    [TestMethod]
    public void Largest_ExcludesDirectoriesAndTombstones()
    {
        var results = LargestFiles.Find(_index, 10, under: null, CancellationToken.None);
        Assert.DoesNotContain(entry => entry.IsDirectory, results);
        Assert.DoesNotContain(entry => entry.Name == "gone.bin", results);
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
        Assert.AreSequenceEqual(
            ["known-big.txt", "known-small.txt"], [.. results.Select(entry => entry.Name)]);
    }

    [TestMethod]
    public void Largest_RespectsTheSubtreeRestriction()
    {
        var documents = FileEntry.Create(_index.CurrentBlock!, _documentsRow);
        var results = LargestFiles.Find(_index, 5, documents, CancellationToken.None);
        Assert.AreSequenceEqual(
            ["huge.bin", "large.bin", "readme.md"], [.. results.Select(entry => entry.Name)]);
    }

    [TestMethod]
    public void Largest_SubtreeIncludesTheAncestorFile()
    {
        var file = _index.Search(new SearchQuery("huge.bin", NameMatchMode.Exact), TestContext.CancellationToken).Single();
        var results = LargestFiles.Find(_index, 5, file, CancellationToken.None);
        Assert.AreSequenceEqual([file.RecordKey], [.. results.Select(entry => entry.RecordKey)]);
    }

    [TestMethod]
    public void Largest_CountLargerThanTheDriveReturnsEverything()
    {
        var results = LargestFiles.Find(_index, 1000, under: null, CancellationToken.None);
        Assert.HasCount(6, results);
    }

    [TestMethod]
    public void Largest_MaximumCountOverSmallIndex_ReturnsOnlyTheAvailableFiles()
    {
        var results = LargestFiles.Find(_index, int.MaxValue, under: null, CancellationToken.None);
        Assert.AreSequenceEqual([9_000_000, 5_000_000, 3_000_000, 1_000, 20, 10], [.. results.Select(entry => entry.Size)]);
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
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 3, under: null, token, completed =>
            {
                assembled = completed;
                if (completed == cancelAfter)
                {
                    cancel();
                }
            }));
        Assert.AreEqual(cancelAfter, assembled);
        Assert.AreEqual(0, _index.CurrentBlock!.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public void Largest_ZeroCountReturnsEmpty()
    {
        Assert.IsEmpty(LargestFiles.Find(_index, 0, under: null, CancellationToken.None));
    }

    [TestMethod]
    public void Largest_NegativeCountThrows()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => LargestFiles.Find(_index, -1, under: null, CancellationToken.None));
    }

    [TestMethod]
    public void Largest_WithCancelledToken_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 10, under: null, token));
    }

    [TestMethod]
    public void Largest_Under_WithCancelledToken_ThrowsOperationCanceled()
    {
        var documents = FileEntry.Create(_index.CurrentBlock!, _documentsRow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            LargestFiles.Find(_index, 10, under: documents, token));
    }

    [TestMethod]
    public void Largest_Under_DefaultAncestor_ThrowsArgumentException()
    {
        var invalidAncestor = default(FileEntry);
        Assert.ThrowsExactly<ArgumentException>(
            () => LargestFiles.Find(_index, 5, invalidAncestor, CancellationToken.None));
    }

    [TestMethod]
    public void Largest_Under_InvalidAncestor_WithCancelledToken_ThrowsOperationCanceled()
    {
        var invalidAncestor = default(FileEntry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(() =>
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
        Assert.AreSequenceEqual(
            [.. Enumerable.Range(0, 5).Select(offset => $"file{LargeRowCount - offset}.bin")], [.. results.Select(entry => entry.Name)]);
    }

    public TestContext TestContext { get; set; } = null!;
}
