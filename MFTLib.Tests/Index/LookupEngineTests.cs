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
    SyntheticBlockBuilder _secondBuilder = null!;
    Snapshot _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _firstBuilder = new SyntheticBlockBuilder();
        var firstRoot = _firstBuilder.AddRoot();
        var documents = _firstBuilder.AddRow("Documents", firstRoot, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        _firstBuilder.AddRow("report.pdf", documents, RowFlags.InUse, 4096, Moment, sequenceNumber: 0);
        _firstBuilder.AddRow("readme.md", documents, RowFlags.InUse, 12, Moment, sequenceNumber: 0);
        _firstBuilder.Complete(Moment);

        _secondBuilder = new SyntheticBlockBuilder('U');
        var secondRoot = _secondBuilder.AddRoot();
        _secondBuilder.AddRow("readme.md", secondRoot, RowFlags.InUse, 15, Moment, sequenceNumber: 0);
        _secondBuilder.Complete(Moment);

        var firstBlock = _firstBuilder.OpenForReading(out _)!;
        var secondBlock = _secondBuilder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([
            new DriveBlock('T', 0, firstBlock, rootDirectoryPath: TestDriveRoot.For('T')),
            new DriveBlock('U', 1, secondBlock, rootDirectoryPath: TestDriveRoot.For('U'))
        ]);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _snapshot.ReleaseNowAsync();
        _firstBuilder.Dispose();
        _secondBuilder.Dispose();
    }


    [TestMethod]
    public void Find_RoundTripsWhateverPathEmits()
    {
        var report = LookupEngine.Find(_snapshot,
            Path.Combine(TestDriveRoot.For('T'), "Documents", "report.pdf"))!.Value;

        var roundTripped = LookupEngine.Find(_snapshot, report.Path);

        Assert.IsTrue(roundTripped.HasValue);
        Assert.AreEqual(report.Path, roundTripped.Value.Path);
        Assert.AreEqual(report.Id, roundTripped.Value.Id);
    }

    [TestMethod]
    public void Find_APathUnderNoIndexedRootReturnsNull()
    {
        Assert.IsNull(LookupEngine.Find(_snapshot,
            Path.Combine(TestDriveRoot.For('Z'), "Documents", "report.pdf")));
        Assert.IsNull(LookupEngine.Find(_snapshot, "not-a-path"));
        Assert.IsNull(LookupEngine.Find(_snapshot, ""));
    }

    [TestMethod]
    public void Find_TheRootDirectoryItselfReturnsTheRootRow()
    {
        var entry = LookupEngine.Find(_snapshot, TestDriveRoot.For('T'));

        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(TestDriveRoot.For('T'), entry.Value.Path);
    }

    [TestMethod]
    public void Find_ResolvesAFullPathToItsEntry()
    {
        var entry = LookupEngine.Find(_snapshot, Path.Combine(TestDriveRoot.For('T'), "Documents", "report.pdf"));
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual("report.pdf", entry.Value.Name);
        Assert.AreEqual(4096L, entry.Value.Size);
    }

    [TestMethod]
    public void Find_MissingSegmentReturnsNull()
    {
        Assert.IsNull(LookupEngine.Find(_snapshot, Path.Combine(TestDriveRoot.For('T'), "Documents", "nothing.txt")));
        Assert.IsNull(LookupEngine.Find(_snapshot, Path.Combine(TestDriveRoot.For('T'), "Nowhere", "report.pdf")));
    }


    [TestMethod]
    public void FindByName_SpansEveryDriveInTheSnapshot()
    {
        var results = LookupEngine.FindByName(_snapshot, "readme.md", caseSensitive: false);
        Assert.AreEqual(2, results.Count);
        CollectionAssert.AreEquivalent(new[] { 'T', 'U' }, results.Select(entry => entry.Id.DriveLetter).ToArray());
    }

    [TestMethod]
    public void FindByName_IsAnExactNameMatchNotASubstring()
    {
        Assert.AreEqual(0, LookupEngine.FindByName(_snapshot, "readme", caseSensitive: false).Count);
        Assert.AreEqual(2, LookupEngine.FindByName(_snapshot, "README.MD", caseSensitive: false).Count);
        Assert.AreEqual(0, LookupEngine.FindByName(_snapshot, "README.MD", caseSensitive: true).Count);
    }

    [TestMethod]
    public void Root_ReturnsTheDriveRootForEachDrive()
    {
        Assert.AreEqual(TestDriveRoot.For('T'), LookupEngine.Root(_snapshot, 'T').Path);
        Assert.AreEqual(TestDriveRoot.For('U'), LookupEngine.Root(_snapshot, 'u').Path);
    }

    [TestMethod]
    public void Root_UnknownDriveThrows()
    {
        Assert.ThrowsException<ArgumentException>(() => LookupEngine.Root(_snapshot, 'Z'));
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
        Assert.AreEqual(BlockValidationResult.Valid, validation);
        var snapshot = Snapshot.Create([new DriveBlock('V', 0, block, rootDirectoryPath: TestDriveRoot.For('V'))]);
        try
        {
            Assert.AreEqual(rootRow, LookupEngine.Root(snapshot, 'V').RowIndex);
            var entry = LookupEngine.Find(snapshot, Path.Combine(TestDriveRoot.For('V'), "Documents", "report.pdf"));
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual("report.pdf", entry.Value.Name);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public void Root_UsesTheHeaderRootRow_NotRowZero()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        using var block = builder.OpenForReading(out var validation)!;
        Assert.AreEqual(BlockValidationResult.Valid, validation);
        var driveBlock = new DriveBlock('T', 0, block, rootDirectoryPath: TestDriveRoot.For('T'));
        var snapshot = Snapshot.Create([driveBlock]);

        var root = LookupEngine.Root(snapshot, 'T');

        Assert.AreEqual(5u, root.RowIndex);
    }

    [TestMethod]
    public void Find_DescendsFromTheHeaderRootRow_NotRowZero()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        using var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock('T', 0, block, rootDirectoryPath: TestDriveRoot.For('T'));
        var snapshot = Snapshot.Create([driveBlock]);

        var found = LookupEngine.Find(snapshot, Path.Combine(TestDriveRoot.For('T'), "documents", "notes.txt"));

        Assert.IsNotNull(found);
        Assert.AreEqual("notes.txt", found.Value.Name);
        Assert.AreEqual(99L, found.Value.Size);
    }

    /// <summary>
    ///     The sequential reference the partitioned scan is held to: one drive at a time in
    ///     snapshot order, rows ascending within each drive.
    /// </summary>
    static List<FileEntry> SequentialFindByName(Snapshot snapshot, string name, bool caseSensitive)
    {
        var reference = new List<FileEntry>();
        foreach (var driveBlock in snapshot.DriveBlocks)
        {
            var scanner = new RowScanner(snapshot, driveBlock.DriveOrdinal);
            while (scanner.MoveNext())
            {
                ref readonly var row = ref scanner.Current;
                if (row.IsInUse && !row.IsDeleted &&
                    NameMatching.EqualsName(scanner.CurrentName, name, caseSensitive))
                {
                    reference.Add(FileEntry.Create(snapshot, driveBlock.DriveOrdinal,
                        scanner.CurrentRowIndex));
                }
            }
        }

        return reference;
    }

    [TestMethod]
    public async Task FindByName_OverPartitionedBlocks_MatchesTheSequentialScanExactly()
    {
        const int fileCount = (int)ScanPartitioning.SingleThreadedRowThreshold + 1024;
        using var firstBuilder = new SyntheticBlockBuilder(slotCapacity: (uint)fileCount + 8,
            namePoolCapacity: (uint)fileCount * 32);
        using var secondBuilder = new SyntheticBlockBuilder('U', slotCapacity: (uint)fileCount + 8,
            namePoolCapacity: (uint)fileCount * 32);
        var firstRoot = firstBuilder.AddRoot();
        var secondRoot = secondBuilder.AddRoot();
        for (var index = 0; index < fileCount; index++)
        {
            firstBuilder.AddRow($"file{index}.dat", firstRoot, RowFlags.InUse, index, Moment,
                sequenceNumber: 0);
            secondBuilder.AddRow($"file{index}.dat", secondRoot, RowFlags.InUse, index, Moment,
                sequenceNumber: 0);
        }

        // One matching name planted at rows spread across both blocks, so it lands in several
        // blocks and in several partitions within each block.
        foreach (var needleRow in new[] { 100u, 16000u, 33000u })
        {
            firstBuilder.AddRowAt(needleRow, "needle.txt",
                new RowColumns(firstRoot, RowFlags.InUse, 0, 1, Moment.Ticks, SequenceNumber: 0));
        }

        foreach (var needleRow in new[] { 42u, 20000u })
        {
            secondBuilder.AddRowAt(needleRow, "needle.txt",
                new RowColumns(secondRoot, RowFlags.InUse, 0, 1, Moment.Ticks, SequenceNumber: 0));
        }

        firstBuilder.Complete(Moment);
        secondBuilder.Complete(Moment);
        var firstBlock = firstBuilder.OpenForReading(out _)!;
        var secondBlock = secondBuilder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([
            new DriveBlock('T', 0, firstBlock),
            new DriveBlock('U', 1, secondBlock)
        ]);
        try
        {
            var partitioned = LookupEngine.FindByName(snapshot, "needle.txt",
                caseSensitive: false);
            var reference = SequentialFindByName(snapshot, "needle.txt", caseSensitive: false);

            Assert.AreEqual(5, partitioned.Count);
            CollectionAssert.AreEqual(
                new[] { ('T', 100u), ('T', 16000u), ('T', 33000u), ('U', 42u), ('U', 20000u) },
                partitioned.Select(entry => (entry.Id.DriveLetter, entry.RowIndex)).ToArray());
            CollectionAssert.AreEqual(
                reference.Select(entry => (entry.Id.DriveLetter, entry.RowIndex)).ToArray(),
                partitioned.Select(entry => (entry.Id.DriveLetter, entry.RowIndex)).ToArray());
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    /// <summary>
    ///     Cancellation is checked at entry, throwing OperationCanceledException
    ///     before any work begins.
    /// </summary>
    [TestMethod]
    public void FindByName_WithACancelledToken_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.ThrowsException<OperationCanceledException>(
            () => LookupEngine.FindByName(_snapshot, "needle.txt", caseSensitive: false, token));
    }

    /// <summary>
    ///     A block whose producer wrote no rows at all has a zero row count, so the
    ///     partitioner returns no ranges and the scan completes empty rather than
    ///     touching the partition machinery.
    /// </summary>
    [TestMethod]
    public async Task FindByName_OverAnEmptyDriveBlock_ReturnsNoResults()
    {
        using var builder = new SyntheticBlockBuilder('Y');
        var snapshot = Snapshot.Create([new DriveBlock('Y', 0, builder.OpenForWriting())]);
        try
        {
            Assert.AreEqual(0u, builder.OpenForWriting().Header.RowCount);
            Assert.AreEqual(0,
                LookupEngine.FindByName(snapshot, "needle.txt", caseSensitive: false).Count);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
