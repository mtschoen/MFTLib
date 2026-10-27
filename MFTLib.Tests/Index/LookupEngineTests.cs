using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                    "throughout rather than IDisposable on the test class itself.")]
public class LookupEngineTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _firstBuilder = null!;
    DriveBlock _driveBlock = null!;

    [TestInitialize]
    public void Initialize()
    {
        _firstBuilder = new SyntheticBlockBuilder();
        var firstRoot = _firstBuilder.AddRoot();
        var documents = _firstBuilder.AddRow("Documents", firstRoot, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        _firstBuilder.AddRow("report.pdf", documents, RowFlags.InUse, 4096, Moment, sequenceNumber: 0);
        _firstBuilder.AddRow("readme.md", documents, RowFlags.InUse, 12, Moment, sequenceNumber: 0);
        _firstBuilder.Complete(Moment);

        var firstBlock = _firstBuilder.OpenForReading(out _)!;
        _driveBlock = new DriveBlock(new VolumeKey('T'), firstBlock, rootDirectoryPath: TestDriveRoot.For('T'));
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _driveBlock.ReleaseNowAsync();
        _firstBuilder.Dispose();
    }

    [TestMethod]
    public void Find_RoundTripsWhateverPathEmits()
    {
        var report = LookupEngine.Find(_driveBlock,
            Path.Combine(TestDriveRoot.For('T'), "Documents", "report.pdf"), TestContext.CancellationToken)!.Value;

        var roundTripped = LookupEngine.Find(_driveBlock, report.Path, TestContext.CancellationToken);

        Assert.IsTrue(roundTripped.HasValue);
        Assert.AreEqual(report.Path, roundTripped.GetValueOrDefault().Path);
        Assert.AreEqual(report.RecordKey, roundTripped.GetValueOrDefault().RecordKey);
    }

    [TestMethod]
    public void Find_APathUnderNoIndexedRootReturnsNull()
    {
        Assert.IsNull(LookupEngine.Find(_driveBlock,
            Path.Combine(TestDriveRoot.For('Z'), "Documents", "report.pdf"), TestContext.CancellationToken));
        Assert.IsNull(LookupEngine.Find(_driveBlock, "not-a-path", TestContext.CancellationToken));
        Assert.IsNull(LookupEngine.Find(_driveBlock, "", TestContext.CancellationToken));
    }

    [TestMethod]
    public void Find_TheRootDirectoryItselfReturnsTheRootRow()
    {
        var entry = LookupEngine.Find(_driveBlock, TestDriveRoot.For('T'), TestContext.CancellationToken);

        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(TestDriveRoot.For('T'), entry.GetValueOrDefault().Path);
    }

    [TestMethod]
    public void Find_ResolvesAFullPathToItsEntry()
    {
        var entry = LookupEngine.Find(_driveBlock, Path.Combine(TestDriveRoot.For('T'), "Documents", "report.pdf"), TestContext.CancellationToken);
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual("report.pdf", entry.GetValueOrDefault().Name);
        Assert.AreEqual(4096L, entry.GetValueOrDefault().Size);
    }

    [TestMethod]
    public void Find_MissingSegmentReturnsNull()
    {
        Assert.IsNull(LookupEngine.Find(_driveBlock, Path.Combine(TestDriveRoot.For('T'), "Documents", "nothing.txt"), TestContext.CancellationToken));
        Assert.IsNull(LookupEngine.Find(_driveBlock, Path.Combine(TestDriveRoot.For('T'), "Nowhere", "report.pdf"), TestContext.CancellationToken));
    }

    [TestMethod]
    public void SearchExact_IsAnExactNameMatchNotASubstring()
    {
        Assert.IsEmpty(SearchEngine.Search(_driveBlock, new SearchQuery("readme", NameMatchMode.Exact), TestContext.CancellationToken));
        Assert.HasCount(1, SearchEngine.Search(_driveBlock, new SearchQuery("README.MD", NameMatchMode.Exact), TestContext.CancellationToken));
        Assert.IsEmpty(SearchEngine.Search(_driveBlock, new SearchQuery("README.MD", NameMatchMode.Exact, CaseSensitive: true), TestContext.CancellationToken));
    }

    [TestMethod]
    public void Root_ReturnsTheDriveRoot()
    {
        Assert.AreEqual(TestDriveRoot.For('T'), LookupEngine.Root(_driveBlock, TestContext.CancellationToken).Path);
    }

    [TestMethod]
    public async Task RootAndFind_UseTheRootRowDeclaredByTheBlockHeader()
    {
        using var builder = new SyntheticBlockBuilder('V');
        for (var metadataRow = 0u; metadataRow < 5; metadataRow++)
        {
            builder.AddRow($"metadata-{metadataRow}", metadataRow, RowFlags.InUse, 0, Moment, sequenceNumber: 0);
        }

        var rootRow = builder.AddRow("", 5, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        var documentsRow = builder.AddRow("Documents", rootRow,
            RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        builder.AddRow("report.pdf", documentsRow, RowFlags.InUse, 4096, Moment, sequenceNumber: 0);
        builder.MutateHeader((ref header) => header.RootRow = rootRow);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out var validation)!;
        Assert.IsNull(validation);
        var driveBlock = new DriveBlock(new VolumeKey('V'), block, rootDirectoryPath: TestDriveRoot.For('V'));
        try
        {
            Assert.AreEqual(rootRow, LookupEngine.Root(driveBlock, TestContext.CancellationToken).RowIndex);
            var entry = LookupEngine.Find(driveBlock, Path.Combine(TestDriveRoot.For('V'), "Documents", "report.pdf"), TestContext.CancellationToken);
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual("report.pdf", entry.GetValueOrDefault().Name);
        }
        finally
        {
            await driveBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Root_UsesTheHeaderRootRow_NotRowZero()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        using var block = builder.OpenForReading(out var validation)!;
        Assert.IsNull(validation);
        var driveBlock = new DriveBlock(new VolumeKey('T'), block, rootDirectoryPath: TestDriveRoot.For('T'));

        var root = LookupEngine.Root(driveBlock, TestContext.CancellationToken);

        Assert.AreEqual(5u, root.RowIndex);
    }

    [TestMethod]
    public void Find_DescendsFromTheHeaderRootRow_NotRowZero()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        using var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(new VolumeKey('T'), block, rootDirectoryPath: TestDriveRoot.For('T'));

        var found = LookupEngine.Find(driveBlock, Path.Combine(TestDriveRoot.For('T'), "documents", "notes.txt"), TestContext.CancellationToken);

        Assert.IsNotNull(found);
        Assert.AreEqual("notes.txt", found.Value.Name);
        Assert.AreEqual(99L, found.Value.Size);
    }

    /// <summary>
    ///     The sequential reference the partitioned scan is held to: rows ascending within the block.
    /// </summary>
    static List<FileEntry> SequentialExactName(DriveBlock driveBlock, string name, bool caseSensitive)
    {
        var reference = new List<FileEntry>();
        var scanner = new RowScanner(driveBlock);
        while (scanner.MoveNext())
        {
            ref readonly var row = ref scanner.Current;
            if (row.IsInUse && !row.IsDeleted &&
                NameMatching.EqualsName(scanner.CurrentName, name, caseSensitive))
            {
                reference.Add(FileEntry.Create(driveBlock, scanner.CurrentRowIndex));
            }
        }

        return reference;
    }

    [TestMethod]
    public async Task SearchExact_OverPartitionedBlock_MatchesTheSequentialScanExactly()
    {
        const int fileCount = (int)ScanPartitioning.SingleThreadedRowThreshold + 1024;
        using var builder = new SyntheticBlockBuilder(slotCapacity: (uint)fileCount + 8,
            namePoolCapacity: (uint)fileCount * 32);
        var root = builder.AddRoot();
        for (var index = 0; index < fileCount; index++)
        {
            builder.AddRow($"file{index}.dat", root, RowFlags.InUse, index, Moment,
                sequenceNumber: 0);
        }

        foreach (var needleRow in new[] { 100u, 16000u, 33000u })
        {
            builder.AddRowAt(needleRow, "needle.txt",
                new RowColumns(root, RowFlags.InUse, 0, 1, Moment.Ticks, SequenceNumber: 0));
        }

        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(new VolumeKey('T'), block);
        try
        {
            var partitioned = SearchEngine.Search(driveBlock, new SearchQuery("needle.txt", NameMatchMode.Exact), TestContext.CancellationToken);
            var reference = SequentialExactName(driveBlock, "needle.txt", caseSensitive: false);

            Assert.HasCount(3, partitioned);
            Assert.AreSequenceEqual(
                [("T", 100u), ("T", 16000u), ("T", 33000u)], [.. partitioned.Select(entry => (entry.RecordKey.Drive.ToString(), entry.RowIndex))]);
            Assert.AreSequenceEqual(
                [.. reference.Select(entry => (entry.RecordKey.Drive, entry.RowIndex))], [.. partitioned.Select(entry => (entry.RecordKey.Drive, entry.RowIndex))]);
        }
        finally
        {
            await driveBlock.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void SearchExact_WithACancelledToken_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsExactly<OperationCanceledException>(
            () => SearchEngine.Search(_driveBlock, new SearchQuery("needle.txt", NameMatchMode.Exact), token));
    }

    [TestMethod]
    public async Task SearchExact_OverAnEmptyDriveBlock_ReturnsNoResults()
    {
        using var builder = new SyntheticBlockBuilder('Y');
        var driveBlock = new DriveBlock(new VolumeKey('Y'), builder.OpenForWriting());
        try
        {
            Assert.AreEqual(0u, builder.OpenForWriting().Header.RowCount);
            Assert.IsEmpty(SearchEngine.Search(driveBlock, new SearchQuery("needle.txt", NameMatchMode.Exact), TestContext.CancellationToken));
        }
        finally
        {
            await driveBlock.ReleaseNowAsync();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
