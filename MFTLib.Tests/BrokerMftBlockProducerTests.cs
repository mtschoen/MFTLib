using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerMftBlockProducerTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ReadJournal = CatchUpSources.ToTip(AdvancedCursor, JournalEntries.Create(20, 12400, "file.txt"))
        }));
        var request = Request(Target());

        var (block, skippedRecordCount) = await ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken);

        var section = broker.Sections.Single();
        Assert.AreSame(section.Block, block);
        Assert.AreEqual(request.BlockPath, block.Path);
        Assert.IsTrue(File.Exists(request.BlockPath));
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, block.Header.ProducerKind);
        Assert.AreEqual(5u, block.Header.RootRow);
        Assert.AreEqual(123u, block.Header.VolumeSerial);
        Assert.AreEqual(ArmedCursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
        Assert.AreEqual(0, skippedRecordCount);
        Assert.IsFalse(block.Header.IsCapacityExhausted);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(block, 20).ToString());
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        Assert.AreEqual(21u, block.Header.RowCount);
        Assert.AreEqual(9u, block.Header.NamePoolUsed);
        Assert.AreEqual(21u, block.RowCount, "the client adopts the cursors the broker published");
        Assert.AreEqual(9u, block.NamePoolUsed, "the client adopts the cursors the broker published");

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        Assert.IsTrue(block.Header.IsComplete);
        var path = block.Path;
        block.Dispose();
        Assert.IsFalse(File.Exists(path), "the block the producer returns is delete-on-close");
    }

    [TestMethod]
    [DataRow("Declined", MftUnavailableReason.ElevationDeclined)]
    [DataRow("Timeout", MftUnavailableReason.BrokerUnavailable)]
    [DataRow("Ended", MftUnavailableReason.BrokerUnavailable)]
    public async Task Produce_AConnectFailureIsAnEligibilityReason(string failure, MftUnavailableReason expected)
    {
        Exception connectFailure = failure switch
        {
            "Declined" => new BrokerLaunchDeclinedException("Failed to launch the elevated broker."),
            "Timeout" => new TimeoutException("The broker never connected."),
            _ => new BrokerEndedException("The elevated broker process for this session has ended.")
        };
        var source = new BrokerMftBlockProducer(_ => Task.FromException<BrokerProcess>(connectFailure)).CreateIndexSource();

        var exception = await Assert.ThrowsExactlyAsync<MftUnavailableException>(
            () => source.Producer(Request(Target()), TestContext.CancellationToken));

        Assert.AreEqual(expected, exception.Reason);
        Assert.AreSame(connectFailure, exception.InnerException);
    }

    [TestMethod]
    public async Task Produce_AnIOExceptionFromConnectPropagatesUnchanged()
    {
        var failure = new IOException("the pipe broke");
        var source = new BrokerMftBlockProducer(_ => Task.FromException<BrokerProcess>(failure)).CreateIndexSource();

        var thrown = await Assert.ThrowsExactlyAsync<IOException>(
            () => source.Producer(Request(Target()), TestContext.CancellationToken));

        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    [DataRow("Incomplete")]
    [DataRow("WrongVolumeSerial")]
    [DataRow("ProducerKind")]
    [DataRow("RowCount")]
    [DataRow("cursor next usn")]
    [DataRow("cursor journal id")]
    public async Task Produce_RejectsInvalidHeaderAndDisposesBlock(string check)
    {
        await using var broker = CreateBrokerChangingBlock(block =>
        {
            switch (check)
            {
                case "Incomplete": block.Header.Flags &= ~BlockFlags.Complete; break;
                case "WrongVolumeSerial": block.Header.VolumeSerial++; break;
                case "ProducerKind": block.Header.ProducerKind = ProducerKind.Enumeration; break;
                case "RowCount": block.Header.RowCount = 0; break;
                case "cursor next usn": block.Header.UsnNextUsn++; break;
                case "cursor journal id": block.Header.UsnJournalIdentifier++; break;
            }
        });
        var request = Request(Target());

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken));

        var expectedMessage = check.StartsWith("cursor ", StringComparison.Ordinal) ? "journal cursor" : check;
        Assert.Contains(expectedMessage, exception.Message);
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_PublishedCapacityDiffersFromTheClientBlock_FailsAndDisposesBlock()
    {
        // A header that shrinks the slot capacity with matching offsets still fits the file, so
        // only the client's own capacities expose it.
        await using var broker = CreateBrokerChangingBlock(block =>
        {
            ref var header = ref block.Header;
            header.SlotCapacity = header.RowCount;
            header.SequenceRegionOffset = (ulong)BlockLayout.SequenceRegionOffset(header.SlotCapacity);
            header.NamePoolOffset = (ulong)BlockLayout.NamePoolOffset(header.SlotCapacity, header.NameEntryCapacity, header.DeltaCapacity);
        });
        var request = Request(Target());

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains(nameof(BlockValidationResult.InconsistentRegions), exception.Message);
        var section = broker.Sections.Single();
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_MismatchedCacheTagFailsAndDisposesBlock()
    {
        // The broker adapter itself, not just the caller, must catch a block whose stored tag
        // does not match what was requested: a mismatch is a producer failure, so it must not
        // be handed back as an adopted block.
        var requested = new CacheTag("GITW", 7);
        var stored = new CacheTag("FILE", 1);
        await using var broker = CreateBrokerChangingBlock(block =>
        {
            block.Header.CacheTagFourCc = stored.PackedFourCc;
            block.Header.CacheTagVersion = stored.Version;
        });
        var request = Request(Target()) with { CacheTag = requested };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("cache tag", exception.Message);
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_BrokerErrorIsReportedAndPendingBlockDisposed()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanVolume = (_, _, _, _, _, _) =>
                throw new IOException("batch failed")
        }));
        var request = Request(Target());

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken));

        // The per-volume error reaches the caller as this exception.
        Assert.Contains("batch failed", exception.Message);
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
    }

    [TestMethod]
    public async Task ProduceAsync_ForwardsBrokerProgressOntoTheIndexProgressChannel()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation
        }));
        var samples = new List<IndexScanProgress>();
        var request = Request(Target()) with { Progress = new SynchronousProgress<IndexScanProgress>(samples.Add) };

        var result = await ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken);
        result.Block.Dispose();

        Assert.IsNotEmpty(samples, "the broker reported no progress to the index channel");
        Assert.IsTrue(samples.All(sample => sample.Root == new VolumeKey('C')));
        Assert.Contains(sample => sample.Phase == IndexScanPhase.Transferring, samples);
        Assert.IsTrue(samples.All(sample => sample.ExpectedRowCount is null || sample.ExpectedRowCount.Value >= sample.WrittenRowCount),
            "when present, total rows should not be less than rows written");
    }

    [TestMethod]
    public async Task ProduceAsync_WithoutRequestProgressChannel_CompletesUsableBlock()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation
        }));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.IsTrue(block.Header.IsComplete);
        Assert.IsGreaterThan(0u, block.Header.RowCount);
    }

    [TestMethod]
    public async Task Produce_ReportsRecordsTheRowWriterCouldNotPlace()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanVolume = (_, _, _, _, _, _) =>
                [[BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(20, "file.txt"), BrokerHostFactory.Record(21, ""), BrokerHostFactory.Record((ulong)uint.MaxValue + 1, "overflow")]]
        }));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.AreEqual(2, result.SkippedRecordCount);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(block, 20).ToString());
        Assert.IsFalse(block.Header.IsCapacityExhausted);
    }

    [TestMethod]
    public async Task Produce_ReportsCapacityExhaustionFlag()
    {
        await using var broker = CreateBrokerChangingBlock(block => block.Header.Flags |= BlockFlags.CapacityExhausted);

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.IsTrue(result.Block.Header.IsCapacityExhausted);
    }

    // 100000 records plan about 3 MB of name pool; 30000 distinct 255-character names need more.
    [TestMethod]
    public async Task Produce_WhenTheNamePoolRunsOut_ScansAgainIntoAGrownSectionAndPublishesAWholeBlock()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            ScanVolume = (_, _, _, _, _, _) => DistinctNameBatches()
        }));
        var request = Request(Target());

        var result = await ProduceAsync(broker.Process, request).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.IsFalse(block.Header.IsCapacityExhausted);
        Assert.AreEqual(30006u, block.Header.RowCount, "rows are indexed by record number, so the count is the last record number plus one");
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.HasCount(2, broker.Sections.All(), "the exhausted section is replaced by a second scan's section");
        Assert.IsTrue(broker.Sections.All().Any(section => ReferenceEquals(section.Block, block)));
        Assert.IsGreaterThan(MftBlockCapacity.Plan(VolumeInformation, default).NamePoolCapacity,
            block.Header.NamePoolCapacity);
    }

    static IEnumerable<IReadOnlyList<MftRecord>> DistinctNameBatches()
    {
        yield return [BrokerHostFactory.Record(5, ".", 3)];
        for (ulong start = 0; start < 30000; start += 1000)
        {
            var batch = new List<MftRecord>();
            for (var offset = start; offset < start + 1000; offset++)
            {
                batch.Add(BrokerHostFactory.Record(6 + offset,
                    new string('n', 247) + offset.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)));
            }

            yield return batch;
        }
    }

    [TestMethod]
    public async Task Produce_PreservesRequestedCacheTagThroughHostCompletion()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation
        }));
        var tag = new CacheTag("GITW", 7);

        var result = await ProduceAsync(broker.Process, Request(Target()) with { CacheTag = tag }).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;

        Assert.AreEqual(tag, block.Header.CacheTag);
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ArmedCursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
    }

    [TestMethod]
    public void Constructor_WithoutConnectFunction_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new BrokerMftBlockProducer(null!));
    }

    [TestMethod]
    public async Task CreateIndexSource_CarriesTheProducerAndTheWatchSourceOfTheSameConnection()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation
        }));

        var source = new BrokerMftBlockProducer(Connect(broker.Process)).CreateIndexSource();
        Assert.IsNotNull(source.WatchSource);
        var result = await source.Producer(Request(Target()), CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block;
        Assert.IsTrue(block.Header.IsComplete);
    }

    public TestContext TestContext { get; set; } = null!;
}
