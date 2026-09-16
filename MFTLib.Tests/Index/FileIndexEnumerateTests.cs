using System.Runtime.CompilerServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.Enumerate" /> is the streaming form of <see cref="FileIndex.Search" />:
///     the same matches in the same order, yielded one at a time, holding its snapshot borrow from
///     the first row until the enumerator completes or is disposed. These tests pin the parity,
///     the borrow bookkeeping, the laziness, and the cancellation and disposal contract.
/// </summary>
[TestClass]
public class FileIndexEnumerateTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_treeRoot);
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    string FirstRoot => Path.Combine(_treeRoot, "first");

    string SecondRoot => Path.Combine(_treeRoot, "second");

    [TestMethod]
    public async Task Enumerate_MatchesSearchAcrossAMultiDriveIndex()
    {
        await using var index = await OpenTwoDriveIndexAsync();

        AssertStreamingMatchesSearch(index, new SearchQuery(null));
        AssertStreamingMatchesSearch(index, new SearchQuery(null, Directories: false));
        AssertStreamingMatchesSearch(index, new SearchQuery("*.md"));
        AssertStreamingMatchesSearch(index, new SearchQuery("readme"));
        AssertStreamingMatchesSearch(index, new SearchQuery(null, MinimumSize: 100));

        var documents = index.Find(Path.Combine(FirstRoot, "Documents"))!.Value;
        AssertStreamingMatchesSearch(index, new SearchQuery("readme", Under: documents));

        var pictures = index.Find(Path.Combine(SecondRoot, "Pictures"))!.Value;
        AssertStreamingMatchesSearch(index, new SearchQuery(null, Under: pictures));

        AssertStreamingMatchesSearch(index, new SearchQuery(null, Under: default(FileEntry)));
    }

    [TestMethod]
    public async Task Enumerate_CompletedToTheEnd_LeavesNoBorrowOutstanding()
    {
        await using var index = await OpenTwoDriveIndexAsync();

        var count = index.Enumerate(new SearchQuery(null)).Count();

        Assert.IsTrue(count > 0);
        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    /// <summary>
    ///     A foreach that breaks early disposes the enumerator, and that disposal is what
    ///     returns the borrow; the block stays mapped while the enumeration sits between rows.
    /// </summary>
    [TestMethod]
    public async Task Enumerate_DisposedPartwayThrough_ReturnsItsBorrow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        var release = index.CurrentSnapshot.ReleaseState;

        using (var enumerator = index.Enumerate(new SearchQuery(null)).GetEnumerator())
        {
            Assert.IsTrue(enumerator.MoveNext());
            Assert.AreEqual(1, release.OutstandingBorrowCount);
        }

        Assert.AreEqual(0, release.OutstandingBorrowCount);
    }

    /// <summary>
    ///     The laziness contract: the call reads nothing and takes no borrow, and the first
    ///     entry is in the caller's hands while the scan still has rows left to visit.
    ///     To prove that later rows have not yet been visited when the first entry is yielded,
    ///     a candidate whose parent chain exceeds the depth cap sits later in the block.
    ///     Where Search visits every row and throws InvalidDataException before returning,
    ///     Enumerate yields the first entry successfully and only throws when a subsequent
    ///     MoveNext reaches the invalid candidate.
    /// </summary>
    [TestMethod]
    public async Task Enumerate_YieldsTheFirstEntryBeforeTheScanHasVisitedEveryRow()
    {
        await using var index = await OpenSyntheticIndexWithDeepCandidateAsync();
        var release = index.CurrentSnapshot.ReleaseState;
        var query = new SearchQuery("candidate", Under: index.Root('T'));

        // Search materializes the whole match set and fails because it visits the deep candidate up front.
        try
        {
            index.Search(query);
            Assert.Fail("Search on a tree with a candidate beyond depth cap must throw InvalidDataException.");
        }
        catch (InvalidDataException)
        {
            // Expected
        }

        // Enumerate is cold: no rows scanned, no borrow taken.
        var enumerable = index.Enumerate(query);
        Assert.AreEqual(0, release.OutstandingBorrowCount, "the call itself already scanned");

        var enumerator = enumerable.GetEnumerator();
        Assert.AreEqual(0, release.OutstandingBorrowCount, "GetEnumerator already scanned");

        // The first MoveNext yields the shallow candidate before the scan has visited every row.
        Assert.IsTrue(enumerator.MoveNext());
        Assert.AreEqual(1, release.OutstandingBorrowCount, "the borrow was not held across yield");
        Assert.AreEqual("candidate-shallow.dat", enumerator.Current.Name);

        // Advancing to the remaining rows visits the deep candidate, which throws InvalidDataException.
        try
        {
            enumerator.MoveNext();
            Assert.Fail("Advancing to the remaining rows visits the deep candidate, which must throw InvalidDataException.");
        }
        catch (InvalidDataException)
        {
            // Expected
        }

        enumerator.Dispose();
        Assert.AreEqual(0, release.OutstandingBorrowCount);
    }

    /// <summary>
    ///     The enumerable is cold even for a token that is already cancelled: the throw lands
    ///     on the first MoveNext, before the first row, and no borrow was ever taken.
    /// </summary>
    [TestMethod]
    public async Task Enumerate_WithACancelledToken_ThrowsOnTheFirstMoveNextAndTakesNoBorrow()
    {
        await using var index = await OpenTwoDriveIndexAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using var enumerator = index.Enumerate(new SearchQuery(null), cancellation.Token).GetEnumerator();
        try
        {
            enumerator.MoveNext();
            Assert.Fail("The first MoveNext on a cancelled token must throw OperationCanceledException.");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    [TestMethod]
    public async Task Enumerate_OnADisposedIndex_ThrowsAtCallTime()
    {
        var index = await OpenSyntheticIndexAsync(64);
        await index.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(() => index.Enumerate(new SearchQuery(null)));
    }

    /// <summary>
    ///     With more rows after the first yield than one cancellation-check interval, a token
    ///     cancelled after the first entry is observed from the next MoveNext, before the scan
    ///     has visited every row. That the second pull throws rather than answering entry two
    ///     is also the behavioural half of the laziness contract.
    /// </summary>
    [TestMethod]
    public async Task Enumerate_ObservesCancellationMidEnumeration()
    {
        const uint rowCount = 10_000;
        Assert.IsTrue(rowCount > RowScanner.CancellationCheckIntervalRows,
            "the test needs a cancellation checkpoint after the first row");

        await using var index = await OpenSyntheticIndexAsync(rowCount);
        using var cancellation = new CancellationTokenSource();
        using var enumerator = index.Enumerate(new SearchQuery("file"), cancellation.Token).GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext(), "every file row matches the query");

        cancellation.Cancel();

        try
        {
            enumerator.MoveNext();
            Assert.Fail("MoveNext after cancellation must throw OperationCanceledException.");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    /// <summary>
    ///     Parity is order as well as set: the point of the swap is that a consumer sees no
    ///     change but memory, so the path sequence has to match exactly.
    /// </summary>
    static void AssertStreamingMatchesSearch(FileIndex index, SearchQuery query)
    {
        var expected = index.Search(query).Select(entry => entry.Path).ToArray();
        var actual = index.Enumerate(query).Select(entry => entry.Path).ToArray();
        CollectionAssert.AreEqual(expected, actual, $"streaming disagreed with Search for {query}");
    }

    async Task<FileIndex> OpenTwoDriveIndexAsync()
    {
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Documents"));
        Directory.CreateDirectory(Path.Combine(FirstRoot, "Pictures"));
        Directory.CreateDirectory(Path.Combine(SecondRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "readme.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Documents", "report.pdf"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(FirstRoot, "Pictures", "holiday.jpg"), new string('y', 5000));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "notes.md"), new string('z', 300));
        await File.WriteAllTextAsync(Path.Combine(SecondRoot, "Pictures", "readme.md"), "world");

        return await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', FirstRoot, 1), new IndexedDrive('U', SecondRoot, 2)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
    }

    Task<FileIndex> OpenSyntheticIndexAsync(uint rowCount)
    {
        return FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, TestVolumeSerial.GetNext())],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, _) => Task.FromResult(
                new MftBlockProduceResult(BuildSyntheticBlock(request, rowCount), JournalId: 7,
                    NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false))
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Writes an MFT-shaped block whose file rows all match the pattern "file", the same
    ///     shape <see cref="FileIndexDisposeCancelsQueryTests" /> uses for its disposal races.
    /// </summary>
    static BlockFile BuildSyntheticBlock(MftBlockProduceRequest request, uint rowCount)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(rowCount + 8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity((rowCount + 8) * 32),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0,
                    Size: 0, ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));

            for (var rowIndex = 6u; rowIndex < rowCount + 6; rowIndex++)
            {
                writer.TryWriteRow(rowIndex, $"file{rowIndex}.dat",
                    new RowColumns(ParentRow: 5, Flags: RowFlags.InUse, Attributes: 0, Size: rowIndex,
                        ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            }

            writer.SetJournalCursor(7, 4096);
            writer.Complete(FixedMoment);
        }

        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
    }

    /// <summary>
    ///     The documented abandonment caveat: an enumerator dropped without disposal holds
    ///     its borrow until the enumerator is collected, the SnapshotBorrow finalizer returns
    ///     it then, and DisposeAsync waits for it like any other borrow rather than for the
    ///     rest of the process.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_AfterAnEnumeratorIsAbandoned_CompletesOnceItIsCollected()
    {
        var index = await OpenSyntheticIndexAsync(64);

        StartAndAbandonAnEnumeration(index);
        Assert.AreEqual(1, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
        await index.DisposeAsync();
    }

    /// <summary>No-inlined so the abandoned enumerator is not kept reachable by this frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void StartAndAbandonAnEnumeration(FileIndex index)
    {
        var enumerable = index.Enumerate(new SearchQuery("file"));
        var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod(nameof(System.Collections.IEnumerable.GetEnumerator))!;
        var enumerator = (System.Collections.IEnumerator)getEnumerator.Invoke(enumerable, null)!;
        _ = enumerator.MoveNext();
    }

    /// <summary>
    ///     A cancellation token cancelled after the last matching entry was yielded is observed
    ///     on the completing MoveNext, mirroring Search's final cancellation check.
    /// </summary>
    [TestMethod]
    public async Task Enumerate_WithTokenCancelledAfterLastYieldedEntry_ThrowsOnNextMoveNext()
    {
        await using var index = await OpenSyntheticIndexAsync(1);
        using var cancellation = new CancellationTokenSource();
        using var enumerator = index.Enumerate(new SearchQuery("file"), cancellation.Token).GetEnumerator();

        Assert.IsTrue(enumerator.MoveNext());
        Assert.AreEqual("file6.dat", enumerator.Current.Name);

        cancellation.Cancel();

        try
        {
            enumerator.MoveNext();
            Assert.Fail("MoveNext after cancellation must throw OperationCanceledException.");
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        Assert.AreEqual(0, index.CurrentSnapshot.ReleaseState.OutstandingBorrowCount);
    }

    Task<FileIndex> OpenSyntheticIndexWithDeepCandidateAsync()
    {
        return FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, TestVolumeSerial.GetNext())],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, _) => Task.FromResult(
                new MftBlockProduceResult(BuildSyntheticBlockWithDeepCandidate(request), JournalId: 7,
                    NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false))
        }, CancellationToken.None);
    }

    static BlockFile BuildSyntheticBlockWithDeepCandidate(MftBlockProduceRequest request)
    {
        var totalRows = BlockLayout.MaximumPathDepth + 16u;
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(totalRows),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(totalRows * 32),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0,
                    Size: 0, ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));

            // Row 6 is a shallow candidate directly under root.
            writer.TryWriteRow(6, "candidate-shallow.dat",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse, Attributes: 0, Size: 1,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));

            // Directory chain reaching MaximumPathDepth hops from root (5).
            var parent = 5u;
            for (var level = 0u; level < BlockLayout.MaximumPathDepth; level++)
            {
                var directoryRow = 7u + level;
                writer.TryWriteRow(directoryRow, $"dir{level}",
                    new RowColumns(ParentRow: parent, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0,
                        Size: 0, ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
                parent = directoryRow;
            }

            // Candidate row whose parent chain exceeds MaximumPathDepth hops.
            var deepRow = 7u + BlockLayout.MaximumPathDepth;
            writer.TryWriteRow(deepRow, "candidate-deep.dat",
                new RowColumns(ParentRow: parent, Flags: RowFlags.InUse, Attributes: 0, Size: 2,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));

            writer.SetJournalCursor(7, 4096);
            writer.Complete(FixedMoment);
        }

        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
    }
}
