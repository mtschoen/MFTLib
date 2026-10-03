using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerMftBlockProducerTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime()
    {
        await using var broker = new InProcessBroker(CreateHost(
            readJournal: CatchUpSources.ToTip(AdvancedCursor, JournalEntryFactory.Create(20, 12400, "file.txt"))));
        BrokerDriveScanResult? completed = null;
        var request = Request(Target());

        var result = await ProduceAsync(broker.Process, request, scanCompleted: scan => completed = scan).WaitAsync(HangGuard);
        var block = result.Block;

        var section = broker.Sections.Single();
        Assert.AreSame(section.Block, block);
        Assert.AreEqual(request.BlockPath, block.Path);
        Assert.IsTrue(File.Exists(request.BlockPath));
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, block.Header.ProducerKind);
        Assert.AreEqual(5u, block.Header.RootRow);
        Assert.AreEqual(123u, block.Header.VolumeSerial);
        Assert.AreEqual(ArmedCursor.JournalId, result.JournalId);
        Assert.AreEqual(ArmedCursor.NextUsn, result.NextUsn);
        Assert.AreEqual(result.JournalId, block.Header.UsnJournalId);
        Assert.AreEqual(result.NextUsn, block.Header.UsnNextUsn);
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.IsFalse(result.Block.Header.IsCompactionNeeded);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(block, 20).ToString());
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        Assert.IsNotNull(completed);
        var outcome = completed.Block;
        Assert.AreSame(block, outcome.Block);
        Assert.AreEqual(21u, block.Header.RowCount);
        Assert.AreEqual(18u, block.Header.NamePoolUsed);
        Assert.AreEqual(ArmedCursor, completed.ArmedCursor);
        Assert.AreEqual(AdvancedCursor.NextUsn, completed.AdvancedCursor!.Value.NextUsn);

        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        Assert.IsTrue(block.Header.IsComplete);
        var path = block.Path;
        block.Dispose();
        Assert.IsFalse(File.Exists(path), "the block the producer returns is delete-on-close");
    }

    [TestMethod]
    [DataRow("Incomplete")]
    [DataRow("WrongVolumeSerial")]
    [DataRow("ProducerKind")]
    [DataRow("RowCount")]
    [DataRow("journal cursor")]
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
                case "journal cursor": block.Header.UsnNextUsn++; break;
            }
        });
        var request = Request(Target());

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request).WaitAsync(HangGuard));

        StringAssert.Contains(exception.Message, check);
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_MismatchedCacheTagFailsAndDisposesBlock()
    {
        // The broker adapter itself, not just the caller, must catch a block whose stored tag
        // does not match what was requested: a mismatch is a producer failure, so it must not
        // reach the validated-result callback or be handed back as an adopted block.
        var requested = new CacheTag("GITW", 7);
        var stored = new CacheTag("FILE", 1);
        await using var broker = CreateBrokerChangingBlock(block =>
        {
            block.Header.CacheTagFourCc = stored.PackedFourCc;
            block.Header.CacheTagVersion = stored.Version;
        });
        var invocations = 0;
        var request = Request(Target()) with { CacheTag = requested };

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request, scanCompleted: _ => invocations++).WaitAsync(HangGuard));

        StringAssert.Contains(exception.Message, "cache tag");
        Assert.AreEqual(0, invocations, "the callback must not run for a block whose tag does not match the request");
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
        Assert.IsFalse(File.Exists(request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_InvalidHeader_DoesNotInvokeScanCompleted()
    {
        // The producer disposes the block on every failure path, so a callback that ran
        // before validation would hand out a BrokerDriveScanResult whose block is dead by the
        // time the callback returns. Reading through it is undefined behaviour rather
        // than an exception, which is why the callback must not see this result at all.
        await using var broker = CreateBrokerChangingBlock(block => block.Header.RowCount = 0);
        var invocations = 0;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, Request(Target()), scanCompleted: _ => invocations++).WaitAsync(HangGuard));

        StringAssert.Contains(exception.Message, "RowCount");
        Assert.AreEqual(0, invocations, "the callback must not run for a result that failed validation");
        BlockFileAssertions.IsDisposed(broker.Sections.Single().Block);
    }

    [TestMethod]
    public async Task Produce_BrokerErrorIsReportedAndPendingBlockDisposed()
    {
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _) =>
            throw new IOException("batch failed")));
        var invocations = 0;
        var request = Request(Target());

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => ProduceAsync(broker.Process, request, scanCompleted: _ => invocations++).WaitAsync(HangGuard));

        // The per-drive error reaches the caller as this exception. The callback sees only
        // validated results, so a failed drive does not reach it.
        StringAssert.Contains(exception.Message, "batch failed");
        Assert.AreEqual(0, invocations);
        var section = broker.Sections.Single();
        Assert.AreEqual(1, section.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(section.Block);
    }

    [TestMethod]
    public async Task ProduceAsync_ForwardsBrokerProgressOntoTheIndexProgressChannel()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var samples = new List<IndexScanProgress>();
        var request = Request(Target()) with { Progress = new SynchronousProgress<IndexScanProgress>(samples.Add) };

        var result = await ProduceAsync(broker.Process, request).WaitAsync(HangGuard);
        result.Block.Dispose();

        Assert.IsTrue(samples.Count > 0, "the broker reported no progress to the index channel");
        Assert.IsTrue(samples.All(sample => sample.DriveLetter == 'C'));
        Assert.IsTrue(samples.Any(sample => sample.Phase == IndexScanPhase.Transferring));
        Assert.IsTrue(samples.All(sample => sample.TotalRows is null || sample.TotalRows.Value >= sample.RowsWritten),
            "when present, total rows should not be less than rows written");
    }

    [TestMethod]
    public async Task ProduceAsync_KeepsTheCallersOwnBrokerProgressChannel()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var brokerSamples = new List<BrokerScanProgress>();

        var result = await ProduceAsync(broker.Process, Request(Target()), new BrokerScanOptions
        {
            Progress = new SynchronousProgress<BrokerScanProgress>(brokerSamples.Add)
        }).WaitAsync(HangGuard);
        result.Block.Dispose();

        Assert.IsTrue(brokerSamples.Count > 0);
    }

    [TestMethod]
    public async Task Produce_ReportsRecordsTheRowWriterCouldNotPlace()
    {
        await using var broker = new InProcessBroker(CreateHost(scanDrive: (_, _, _, _, _) =>
            [[Record(5, ".", 3), Record(20, "file.txt"), Record(21, ""), Record((ulong)uint.MaxValue + 1, "overflow")]]));

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard);
        using var block = result.Block;

        Assert.AreEqual(2, result.SkippedRecordCount);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(block, 20).ToString());
        Assert.IsFalse(block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public async Task Produce_ReportsCompactionFlag()
    {
        await using var broker = CreateBrokerChangingBlock(block => block.Header.Flags |= BlockFlags.CompactionNeeded);

        var result = await ProduceAsync(broker.Process, Request(Target())).WaitAsync(HangGuard);
        using var block = result.Block;

        Assert.IsTrue(result.Block.Header.IsCompactionNeeded);
    }

    [TestMethod]
    public async Task Produce_PreservesRequestedCacheTagThroughHostCompletion()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var tag = new CacheTag("GITW", 7);

        var result = await ProduceAsync(broker.Process, Request(Target()) with { CacheTag = tag }).WaitAsync(HangGuard);
        using var block = result.Block;

        Assert.AreEqual(tag, block.Header.CacheTag);
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ArmedCursor.JournalId, block.Header.UsnJournalId);
        Assert.AreEqual(ArmedCursor.NextUsn, block.Header.UsnNextUsn);
    }
}
