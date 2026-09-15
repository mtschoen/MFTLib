using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    static async Task<BrokerFrame> ReadControlRequestAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var bytes = new byte[4 + BinaryPrimitives.ReadInt32LittleEndian(header)];
        header.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(4), token);
        return BrokerProtocol.ReadFrame(bytes, out _);
    }

    static async Task SendControlReplyAsync(Stream stream, Action<ArrayBufferWriter<byte>> write,
        CancellationToken token)
    {
        var bytes = new ArrayBufferWriter<byte>();
        write(bytes);
        await stream.WriteAsync(bytes.WrittenMemory, token);
        await stream.FlushAsync(token);
    }

    [TestMethod]
    public async Task ControlExchange_ScanWhileWatching_RoutesQueryAndCatchUpWithoutAnotherReader()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        await using var guarded = new SingleReaderGuardStream(transport);
        await using var client = MakeMinimalFakeClient(guarded);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
        {
            ["D"] = new(9, 100)
        }, token);
        var start = await ReadControlRequestAsync(server, token);
        var epoch = WatchSpecArmEpochs.ForDrive(start, "D");
        await guarded.FirstReadStarted.WaitAsync(token);
        await using var live = client.CreateBatchSource()("D", default, token).GetAsyncEnumerator(token);

        var scan = client.ArmScanAndCatchUpAsync(DriveC, CreateOptions(), token);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes, (await ReadControlRequestAsync(server, token)).Kind);
        await SendControlReplyAsync(server, writer =>
        {
            BrokerProtocol.WriteJournalBatch(writer, "D", epoch, new(9, 110),
                [JournalEntryFactory.Create(30, 105, "during-query.txt")]);
            BrokerProtocol.WriteVolumeInfo(writer, "C", 128, 1024, 128 * 1024);
        }, token);
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await ReadControlRequestAsync(server, token)).Kind);
        await SendControlReplyAsync(server, writer =>
        {
            BrokerProtocol.WriteCursor(writer, "C", new(7, 200));
            BrokerProtocol.WriteJournalBatch(writer, "D", epoch, new(9, 120),
                [JournalEntryFactory.Create(31, 115, "during-scan.txt")]);
            BrokerProtocol.WriteWarning(writer, "C", "catch-up degraded");
            BrokerProtocol.WriteJournalBatch(writer, "C", BrokerFrame.NoArmEpoch, new(7, 210), []);
        }, token);
        var result = await scan.WaitAsync(token);
        Assert.AreEqual(210L, result.AdvancedCursors["C"].NextUsn);
        Assert.AreEqual("catch-up degraded", result.Warnings["C"]);
        Assert.IsFalse(result.CatchUpEntries.ContainsKey("D"));
        Assert.IsTrue(await live.MoveNextAsync().AsTask().WaitAsync(token));
        Assert.AreEqual("during-query.txt", live.Current.Entries.Single().FileName);
        Assert.IsTrue(await live.MoveNextAsync().AsTask().WaitAsync(token));
        Assert.AreEqual("during-scan.txt", live.Current.Entries.Single().FileName);
        Assert.IsFalse(guarded.ConcurrentReadAttempted);
    }
}
