using System.Buffers;
using static MFTLib.Tests.TestSupport.InProcessBlockBrokerHarness;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerMftBlockProducerTests
{
    static readonly UsnJournalCursor ArmedCursor = new(71, 12345);

    [TestMethod]
    public async Task Produce_AdoptsClientBlockAndReleasesOnlySectionLifetime()
    {
        await using var harness = new InProcessBlockBrokerHarness();
        BrokerScanResult? completed = null;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync,
            scanCompleted: result => completed = result).CreateProducer();

        var result = await producer(harness.Request, harness.CancellationToken);
        using var block = result.Block;

        Assert.AreSame(harness.CreatedBlock, block);
        Assert.AreEqual(harness.Request.BlockPath, block.Path);
        Assert.IsTrue(block.DeleteOnClose);
        Assert.IsTrue(block.Header.IsComplete);
        Assert.AreEqual(ProducerKind.Mft, block.Header.ProducerKind);
        Assert.AreEqual(5u, block.Header.RootRow);
        Assert.AreEqual(123u, block.Header.VolumeSerial);
        Assert.AreEqual(ArmedCursor.JournalId, result.JournalId);
        Assert.AreEqual(ArmedCursor.NextUsn, result.NextUsn);
        Assert.AreEqual(result.JournalId, block.Header.UsnJournalId);
        Assert.AreEqual(result.NextUsn, block.Header.UsnNextUsn);
        Assert.AreEqual(0, result.SkippedRecordCount);
        Assert.IsFalse(result.CompactionNeeded);
        Assert.AreEqual("file.txt", NamePool.ReadRowName(block, 20).ToString());
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        Assert.IsNotNull(completed);
        var outcome = completed.BlockOutcomes["C"];
        Assert.AreEqual(harness.SectionName, outcome.SectionName);
        Assert.AreSame(block, outcome.Block);
        Assert.AreEqual(21L, outcome.RowCount);
        Assert.AreEqual(18L, outcome.NamePoolUsedBytes);
        Assert.AreEqual(12500L, completed.AdvancedCursors["C"].NextUsn);
        Assert.AreEqual(1, completed.CatchUpEntries["C"].Length);
        await harness.Client.DisposeAsync();
        harness.ClientDisposed = true;
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        Assert.IsTrue(block.Header.IsComplete);
    }

    [TestMethod]
    [DataRow("Incomplete")]
    [DataRow("WrongVolumeSerial")]
    [DataRow("ProducerKind")]
    [DataRow("RowCount")]
    [DataRow("journal cursor")]
    public async Task Produce_RejectsInvalidHeaderAndDisposesBlock(string check)
    {
        await using var harness = new InProcessBlockBrokerHarness(block =>
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
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync).CreateProducer();

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => producer(harness.Request, harness.CancellationToken));

        StringAssert.Contains(exception.Message, check);
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
        Assert.IsFalse(File.Exists(harness.Request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_InvalidHeader_DoesNotInvokeScanCompleted()
    {
        // The producer disposes the block on every failure path, so a callback that ran
        // before validation would hand out a BrokerScanResult whose block is dead by the
        // time the callback returns. Reading through it is undefined behaviour rather
        // than an exception, which is why the callback must not see this result at all.
        await using var harness = new InProcessBlockBrokerHarness(block => block.Header.RowCount = 0);
        var invocations = 0;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync,
            scanCompleted: _ => invocations++).CreateProducer();

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => producer(harness.Request, harness.CancellationToken));

        StringAssert.Contains(exception.Message, "RowCount");
        Assert.AreEqual(0, invocations, "the callback must not run for a result that failed validation");
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
    }

    [TestMethod]
    public async Task Produce_BrokerErrorIsReportedAndPendingBlockDisposed()
    {
        await using var harness = new InProcessBlockBrokerHarness(sourceFails: true);
        var invocations = 0;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync,
            scanCompleted: _ => invocations++).CreateProducer();

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => producer(harness.Request, harness.CancellationToken));

        // The per-drive error reaches the caller as this exception. The callback sees only
        // validated results, so a failed drive does not reach it.
        StringAssert.Contains(exception.Message, "batch failed");
        Assert.AreEqual(0, invocations);
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
    }

    [TestMethod]
    public async Task Produce_ReportsCompactionFlag()
    {
        await using var harness = new InProcessBlockBrokerHarness(block => block.Header.Flags |= BlockFlags.CompactionNeeded);
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync).CreateProducer();
        var result = await producer(harness.Request, harness.CancellationToken);
        using var block = result.Block;
        Assert.IsTrue(result.CompactionNeeded);
    }

    [TestMethod]
    public async Task Scan_NormalizesTargets()
    {
        await using var harness = new InProcessBlockBrokerHarness();
        var result = await harness.Client.ArmScanAndCatchUpAsync([@"\\.\c:"], new BrokerScanOptions
        {
            BlockTargets = new Dictionary<string, BlockScanTarget>
            {
                [@"c:\"] = new(harness.Request.BlockPath, 123, true)
            },
        }, harness.CancellationToken);
        using var block = result.BlockOutcomes["C"].Block;
        Assert.AreEqual(MftBlockCapacity.Plan(null).SlotCapacity, block.Header.SlotCapacity);
        Assert.AreEqual(MftBlockCapacity.Plan(null).NamePoolCapacity, block.Header.NamePoolCapacity);
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
    }

    [TestMethod]
    public async Task Scan_MissingTargetFailsBeforeAnyTransmission()
    {
        await using var harness = new InProcessBlockBrokerHarness();
        var transmitted = false;
        var exception = await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            harness.Client.ArmScanAndCatchUpAsync(["C", "D"], new BrokerScanOptions
            {
                BlockTargets = new Dictionary<string, BlockScanTarget>
                {
                    ["C"] = new(harness.Request.BlockPath, 123, true)
                }
            }, () => transmitted = true, harness.CancellationToken));
        StringAssert.Contains(exception.Message, "D");
        Assert.IsFalse(transmitted);
        Assert.IsNull(harness.CreatedBlock);
    }

    [TestMethod]
    public async Task Scan_DuplicateSectionNameReleasesBothFactoryResults()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var server = serverSide;
        var blocks = new List<BlockFile>();
        var lifetimes = new List<CountingLifetime>();
        await using var client = new JournalBrokerClient(clientSide,
            (_, options) =>
            {
                blocks.Add(BlockFile.Create(options));
                lifetimes.Add(new CountingLifetime());
                return ("duplicate-section", blocks[^1], lifetimes[^1]);
            });
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", "query failed");
        BrokerProtocol.WriteError(response, "D", "query failed");
        await server.WriteAsync(response.WrittenMemory);
        try
        {
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.ArmScanAndCatchUpAsync(["C", "D"],
                new BrokerScanOptions
                {
                    BlockTargets = new Dictionary<string, BlockScanTarget>
                    {
                        ["C"] = CreateTemporaryTarget(),
                        ["D"] = CreateTemporaryTarget()
                    }
                }));
            CollectionAssert.AreEqual(new[] { 1, 1 }, lifetimes.Select(lifetime => lifetime.DisposeCount).ToArray());
            blocks.ForEach(BlockFileAssertions.IsDisposed);
        }
        finally
        {
            blocks.ForEach(block => block.Dispose());
        }
    }

    static BlockScanTarget CreateTemporaryTarget() =>
        new(Path.Combine(Path.GetTempPath(), $"producer-duplicate-{Guid.NewGuid():N}.bin"), 123, true);

}
