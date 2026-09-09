using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerMftBlockProducerProtocolTests
{
    [TestMethod]
    public async Task Produce_PreservesMaximumSkippedRecordCount()
    {
        await using var harness = new Harness(fault: "MaximumSkippedCount");
        var result = await harness.ProduceAsync();
        using var block = result.Block;
        Assert.AreEqual(int.MaxValue, result.SkippedRecordCount);
    }

    [TestMethod]
    public async Task Produce_SkippedRecordCountOverflowThrowsAndDisposesBlock()
    {
        await using var harness = new Harness(fault: "SkippedCountOverflow");
        await Assert.ThrowsExceptionAsync<OverflowException>(() => harness.ProduceAsync());
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
        Assert.IsFalse(File.Exists(harness.Request.BlockPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scan_SendsBlockSpecificationAndPlansFromVolumeQuery(bool queryFails)
    {
        await using var harness = new Harness(queryFails: queryFails);
        var result = await harness.ProduceAsync();
        using var block = result.Block;
        Assert.AreEqual("C:0:0:protocol-section:0", harness.ScanSpecification);
        var expected = MftBlockCapacity.Plan(queryFails ? null : Harness.VolumeInformation);
        Assert.AreEqual(expected.SlotCapacity, block.Header.SlotCapacity);
        Assert.AreEqual(expected.NamePoolCapacity, block.Header.NamePoolCapacity);
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Produce_DisconnectionDisposesBlockBeforeReturning(bool sendReady)
    {
        await using var harness = new Harness(sendReady, disconnect: true);
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => harness.ProduceAsync());
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
        Assert.IsFalse(File.Exists(harness.Request.BlockPath));
    }

    [TestMethod]
    public async Task Produce_CompletionCallbackFailureDisposesBlock()
    {
        await using var harness = new Harness();
        await Assert.ThrowsExceptionAsync<IOException>(() => harness.ProduceAsync(
            _ => throw new IOException("callback failed")));
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
    }

    [TestMethod]
    [DataRow("MissingOutcome", "No block outcome")]
    [DataRow("MissingCursor", "No armed journal cursor")]
    [DataRow("RepeatedReady", "No pending block")]
    [DataRow("ErrorAfterReady", "scan failed after ready")]
    public async Task Produce_RejectsBrokenExchangeAndDisposesBlock(string fault, string message)
    {
        await using var harness = new Harness(fault: fault);
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => harness.ProduceAsync());
        StringAssert.Contains(exception.Message, message);
        Assert.AreEqual(1, harness.Lifetime.DisposeCount);
        BlockFileAssertions.IsDisposed(harness.CreatedBlock!);
    }

    sealed class Harness : IAsyncDisposable
    {
        public static readonly NtfsVolumeInformation VolumeInformation = new(1024L * 100000, 1024, 0, 0, 0, 0);
        static readonly UsnJournalCursor Cursor = new(71, 12345);
        readonly Stream _server;
        readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
        readonly Task _serving;
        readonly JournalBrokerClient _client;

        public Harness(bool sendReady = true, bool disconnect = false, bool queryFails = false, string fault = "")
        {
            var (client, server) = DuplexStream.CreatePair();
            _server = server;
            _client = new JournalBrokerClient(client,
                (_, options) =>
                {
                    CreatedBlock = BlockFile.Create(options);
                    return ("protocol-section", CreatedBlock, Lifetime);
                });
            _serving = ServeAsync(sendReady, disconnect, queryFails, fault);
        }

        public BlockFile? CreatedBlock { get; private set; }
        public CountingLifetime Lifetime { get; } = new();
        public string? ScanSpecification { get; private set; }
        public MftBlockProduceRequest Request { get; } = new()
        {
            DriveLetter = 'C',
            VolumeSerial = 123,
            DeleteOnClose = true,
            BlockPath = Path.Combine(Path.GetTempPath(), $"producer-protocol-{Guid.NewGuid():N}.bin")
        };

        public Task<MftBlockProduceResult> ProduceAsync(Action<BrokerScanResult>? completed = null) =>
            new BrokerMftBlockProducer(_ => Task.FromResult(_client), scanCompleted: completed)
                .CreateProducer()(Request, _timeout.Token);

        async Task ServeAsync(bool sendReady, bool disconnect, bool queryFails, string fault)
        {
            var query = await ReadFrameAsync();
            Assert.AreEqual(BrokerFrameKind.QueryVolumes, query.Kind);
            var response = new ArrayBufferWriter<byte>();
            if (queryFails)
            {
                BrokerProtocol.WriteError(response, "C", "query failed");
            }
            else
            {
                BrokerProtocol.WriteVolumeInfo(response, "C", VolumeInformation.MftRecordCount,
                    VolumeInformation.BytesPerFileRecordSegment, VolumeInformation.MftValidDataLength);
            }

            await _server.WriteAsync(response.WrittenMemory, _timeout.Token);
            var scan = await ReadFrameAsync();
            Assert.AreEqual(BrokerFrameKind.ArmAndScan, scan.Kind);
            ScanSpecification = scan.DrivesSpec;
            response.Clear();
            if (fault != "MissingCursor")
            {
                BrokerProtocol.WriteCursor(response, "C", Cursor);
            }

            if (sendReady && fault != "MissingOutcome")
            {
                using var writer = new RecordingBlockSectionWriter(_ => CreatedBlock);
                var result = writer.Write("protocol-section", Cursor,
                    [[new MftRecord(5, 5, new MftRecordFields(3), ".", null)]], MftBlockRowFilter.Full, null, _timeout.Token);
                var skippedRecordCount = fault switch
                {
                    "MaximumSkippedCount" => int.MaxValue,
                    "SkippedCountOverflow" => (long)int.MaxValue + 1,
                    _ => result.SkippedRecordCount
                };
                BrokerProtocol.WriteScanReady(response, "protocol-section", result.RowCount, result.NamePoolUsedBytes, skippedRecordCount);
                if (fault == "RepeatedReady")
                {
                    BrokerProtocol.WriteScanReady(response, "protocol-section", result.RowCount, result.NamePoolUsedBytes, result.SkippedRecordCount);
                }
            }

            if (fault == "ErrorAfterReady")
            {
                BrokerProtocol.WriteError(response, "C", "scan failed after ready");
            }
            else if (!disconnect)
            {
                BrokerProtocol.WriteJournalBatch(response, "C", Cursor, []);
            }

            await _server.WriteAsync(response.WrittenMemory, _timeout.Token);
            await _server.DisposeAsync();
        }

        async Task<BrokerFrame> ReadFrameAsync()
        {
            var header = new byte[4];
            await _server.ReadExactlyAsync(header, _timeout.Token);
            var frame = new byte[4 + BinaryPrimitives.ReadInt32LittleEndian(header)];
            header.CopyTo(frame, 0);
            await _server.ReadExactlyAsync(frame.AsMemory(4), _timeout.Token);
            return BrokerProtocol.ReadFrame(frame, out _);
        }

        public async ValueTask DisposeAsync()
        {
            await _serving;
            await _client.DisposeAsync();
            await _server.DisposeAsync();
            CreatedBlock?.Dispose();
            _timeout.Dispose();
        }
    }

    sealed class CountingLifetime : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

}
