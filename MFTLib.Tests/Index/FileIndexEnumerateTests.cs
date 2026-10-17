using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Streaming-specific behavior of <see cref="FileIndex.Enumerate" />: laziness before visiting
///     every row, enumerator abandonment and finalization borrow reclamation, and cancellation after
///     the last matching entry.
/// </summary>
[TestClass]
public sealed class FileIndexEnumerateTests : IDisposable
{
    static readonly DateTime FixedMoment = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    IndexTreeFixture _fixture = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture(seedDocuments: false);

    public void Dispose() => _fixture.Dispose();

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
        var release = index.CurrentSnapshot[0].ReleaseState;
        var query = new SearchQuery("candidate", Subtree: index.Root('T'));

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
        Assert.AreEqual(0, release.Hooks.OutstandingBorrowCount, "the call itself already scanned");

        var enumerator = enumerable.GetEnumerator();
        Assert.AreEqual(0, release.Hooks.OutstandingBorrowCount, "GetEnumerator already scanned");

        // The first MoveNext yields the shallow candidate before the scan has visited every row.
        Assert.IsTrue(enumerator.MoveNext());
        Assert.AreEqual(1, release.Hooks.OutstandingBorrowCount, "the borrow was not held across yield");
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
        Assert.AreEqual(0, release.Hooks.OutstandingBorrowCount);
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

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.AreEqual(0, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
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
        Assert.AreEqual(1, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
        GC.KeepAlive(enumerator);
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

        Assert.AreEqual(0, index.CurrentSnapshot[0].ReleaseState.Hooks.OutstandingBorrowCount);
    }

    Task<FileIndex> OpenSyntheticIndexAsync(uint rowCount) =>
        FileIndex.OpenAsync(_fixture.Options(volumeSerial: TestVolumeSerial.GetNext(), producer: ProducerKind.Mft) with
        {
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(SeededBlocks.Build(request, rowCount, i => $"file{i}.dat", FixedMoment),
                    SkippedRecordCount: 0)))
        }, CancellationToken.None);

    Task<FileIndex> OpenSyntheticIndexWithDeepCandidateAsync() =>
        FileIndex.OpenAsync(_fixture.Options(volumeSerial: TestVolumeSerial.GetNext(), producer: ProducerKind.Mft) with
        {
            MftSource = new MftIndexSource((request, _) => Task.FromResult(
                new MftBlockProduceResult(BuildSyntheticBlockWithDeepCandidate(request),
                    SkippedRecordCount: 0)))
        }, CancellationToken.None);

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
            writer.Complete(FixedMoment, null);
        }

        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
    }
}
