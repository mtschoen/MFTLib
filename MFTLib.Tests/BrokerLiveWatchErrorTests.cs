using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Verifies that an Error frame arriving during live watch faults only the affected
///     drive's channel, leaving other drives streaming and leaving Heartbeat unrouted.
/// </summary>
[TestClass]
public class BrokerLiveWatchErrorTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task LiveWatch_ErrorFrameForDrive_FaultsThatDrivesBatchSource()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(WatchCursors("C"));
        var batchSource = client.CreateBatchSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var startWatch = await ReadOneFrameAsync(serverSide, CancellationToken.None);
        var epochC = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", epochC, "journal wrapped");
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, cts.Token)) { }
        });
        Assert.AreEqual("journal wrapped", exception.Message);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task LiveWatch_ErrorFrameForOneDrive_OtherDrivesKeepStreaming()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(WatchCursors("C", "D"));
        var batchSource = client.CreateBatchSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var cursor = new UsnJournalCursor(7UL, 210L);
        var entry = JournalEntryFactory.Create(1, 110, "f.txt");

        var startWatch = await ReadOneFrameAsync(serverSide, CancellationToken.None);
        var epochC = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "D", WatchSpecArmEpochs.ForDrive(startWatch, "D"), "journal wrapped");
        BrokerProtocol.WriteJournalBatch(response, "C", epochC, cursor, [entry]);
        BrokerProtocol.WriteEndWatchAck(response);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("D:\\", default, cts.Token))
            {
            }
        });

        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        await foreach (var batch in batchSource("C:\\", default, cts.Token))
        {
            received.Add(batch);
        }

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(cursor, received[0].Cursor);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task LiveWatch_ErrorFrameBeforeSubscribe_LateSubscriberGetsFault()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(WatchCursors("C"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var startWatch = await ReadOneFrameAsync(serverSide, CancellationToken.None);
        var epochC = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", epochC, "journal wrapped");
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        // Give the demux a moment to read and route the Error frame before the first
        // subscriber for "C" registers, so the channel is faulted before it exists.
        await Task.Delay(20, cts.Token);

        var batchSource = client.CreateBatchSource();
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, cts.Token))
            {
            }
        });
        Assert.AreEqual("journal wrapped", exception.Message);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task LiveWatch_WarningFrameForDrive_FaultsThatDrivesBatchSourceAndLeavesTheOthers()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(WatchCursors("C", "D"));
        var batchSource = client.CreateBatchSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var cursor = new UsnJournalCursor(7UL, 210L);
        var entry = JournalEntryFactory.Create(1, 110, "f.txt");

        var startWatch = await ReadOneFrameAsync(serverSide, CancellationToken.None);
        var epochC = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteWarning(response, "D", "unexpected live warning");
        BrokerProtocol.WriteJournalBatch(response, "C", epochC, cursor, [entry]);
        BrokerProtocol.WriteEndWatchAck(response);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("D:\\", default, cts.Token))
            {
            }
        });
        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        await foreach (var batch in batchSource("C:\\", default, cts.Token))
        {
            received.Add(batch);
        }

        StringAssert.Contains(exception.Message, nameof(BrokerFrameKind.Warning));
        StringAssert.Contains(exception.Message, "D");
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(cursor, received[0].Cursor);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task LiveWatch_WarningFrameForDrive_DisarmsThatDriveSoALaterBatchForItIsDropped()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(WatchCursors("C"));
        var batchSource = client.CreateBatchSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var driveCEnumeration = batchSource("C", default, cts.Token).GetAsyncEnumerator();
        var driveCMoveNext = driveCEnumeration.MoveNextAsync().AsTask();

        var startWatch = await ReadOneFrameAsync(serverSide, CancellationToken.None);
        var epochC = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteWarning(response, "C", "unexpected live warning");
        BrokerProtocol.WriteJournalBatch(response, "C", epochC, new UsnJournalCursor(7UL, 210L),
            [JournalEntryFactory.Create(1, 110, "stale.txt")]);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            _ = await driveCMoveNext;
        });
        StringAssert.Contains(exception.Message, nameof(BrokerFrameKind.Warning));

        await client.SendStartWatchAsync(WatchCursors("D"));
        var startWatchD = await ReadOneFrameAsync(serverSide, CancellationToken.None);
        var epochD = WatchSpecArmEpochs.ForDrive(startWatchD, "D");
        var driveDEnumeration = batchSource("D", default, cts.Token).GetAsyncEnumerator();
        var driveDMoveNext = driveDEnumeration.MoveNextAsync().AsTask();
        var driveDCursor = new UsnJournalCursor(7UL, 310L);
        response.Clear();
        BrokerProtocol.WriteJournalBatch(response, "D", epochD, driveDCursor, [JournalEntryFactory.Create(2, 301, "d.txt")]);
        BrokerProtocol.WriteEndWatchAck(response);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        Assert.IsTrue(await driveDMoveNext);
        Assert.AreEqual(driveDCursor, driveDEnumeration.Current.Cursor);

        await using var lateDriveCEnumeration = batchSource("C", default, cts.Token).GetAsyncEnumerator();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            _ = await lateDriveCEnumeration.MoveNextAsync();
        });

        await driveCEnumeration.DisposeAsync();
        await driveDEnumeration.DisposeAsync();
        await client.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));
    }

    static Dictionary<string, UsnJournalCursor> WatchCursors(params string[] drives)
    {
        return drives.ToDictionary(d => d, _ => new UsnJournalCursor(7UL, 0L), StringComparer.OrdinalIgnoreCase);
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var totalLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength), cancellationToken);
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }
}
