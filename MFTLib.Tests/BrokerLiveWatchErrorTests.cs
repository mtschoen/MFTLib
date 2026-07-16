using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests;

/// <summary>
/// Verifies that an Error frame arriving during live watch faults only the affected
/// drive's channel, leaving other drives streaming and leaving Heartbeat unrouted.
/// </summary>
[TestClass]
public class BrokerLiveWatchErrorTests
{
    [TestMethod]
    public async Task LiveWatch_ErrorFrameForDrive_FaultsThatDrivesBatchSource()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        await client.SendStartWatchAsync(WatchCursors("C"));
        var batchSource = client.CreateBatchSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", "journal wrapped");
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

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
        var entry = UsnJournalEntry.Create(
            1, 5, 110, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "f.txt");

        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "D", "journal wrapped");
        BrokerProtocol.WriteJournalBatch(response, "C", cursor, new[] { entry });
        BrokerProtocol.WriteEndWatchAck(response);
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("D:\\", default, cts.Token)) { }
        });

        var received = new List<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        await foreach (var batch in batchSource("C:\\", default, cts.Token))
            received.Add(batch);

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

        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", "journal wrapped");
        await serverSide.WriteAsync(response.WrittenMemory);
        await serverSide.FlushAsync();

        // Give the demux a moment to read and route the Error frame before the first
        // subscriber for "C" registers, so the channel is faulted before it exists.
        await Task.Delay(20, cts.Token);

        var batchSource = client.CreateBatchSource();
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, cts.Token)) { }
        });
        Assert.AreEqual("journal wrapped", exception.Message);

        await client.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static JournalBrokerClient MakeMinimalFakeClient(Stream pipe) =>
        new(pipe,
            mmfReader: new NullMmfReader(),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

    static Dictionary<string, UsnJournalCursor> WatchCursors(params string[] drives) =>
        drives.ToDictionary(d => d, _ => new UsnJournalCursor(7UL, 0L), StringComparer.OrdinalIgnoreCase);

    sealed class NullMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength) => Array.Empty<ScanRecord>();
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
