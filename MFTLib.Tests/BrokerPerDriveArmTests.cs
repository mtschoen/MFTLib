using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class BrokerPerDriveArmTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task SendStartWatchAsync_ForASecondDrive_ArmsItWithoutStartingASecondDemux()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        var firstStart = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var demuxTaskField = typeof(JournalBrokerClient).GetField(
            "_demuxTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var firstDemuxTask = (Task)demuxTaskField.GetValue(client)!;

        await client.SendStartWatchAsync(WatchCursors("D"), cancellation.Token);
        var secondStart = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var secondDemuxTask = (Task)demuxTaskField.GetValue(client)!;

        Assert.IsNotNull(firstStart.DrivesSpec);
        Assert.IsTrue(firstStart.DrivesSpec.StartsWith("C:7:100:", StringComparison.Ordinal));
        Assert.IsNotNull(secondStart.DrivesSpec);
        Assert.IsTrue(secondStart.DrivesSpec.StartsWith("D:7:100:", StringComparison.Ordinal));
        Assert.AreSame(firstDemuxTask, secondDemuxTask);

        var batchSource = client.CreateBatchSource();
        await using var driveCEnumerator = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveCMoveNext = driveCEnumerator.MoveNextAsync().AsTask();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();

        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (firstStart, "C", new UsnJournalCursor(7UL, 110L), JournalEntryFactory.Create(1, 101, "c.txt")),
            (secondStart, "D", new UsnJournalCursor(7UL, 210L), JournalEntryFactory.Create(2, 201, "d.txt")));

        Assert.IsTrue(await driveCMoveNext);
        Assert.AreEqual(110L, driveCEnumerator.Current.Cursor.NextUsn);
        Assert.IsTrue(await driveDMoveNext);
        Assert.AreEqual(210L, driveDEnumerator.Current.Cursor.NextUsn);
    }

    [TestMethod]
    public async Task SendStartWatchAsync_ForAnArmedDrive_ReplacesItsChannelSoTheEarlierSubscriberEnds()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        await ReadOneFrameAsync(serverSide, cancellation.Token);

        var batchSource = client.CreateBatchSource();
        var earlier = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        var earlierMoveNext = earlier.MoveNextAsync().AsTask();
        try
        {
            await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 500L) },
                cancellation.Token);
            var start3 = await ReadOneFrameAsync(serverSide, cancellation.Token);
            Assert.IsNotNull(start3.DrivesSpec);
            Assert.IsTrue(start3.DrivesSpec.StartsWith("C:7:500:", StringComparison.Ordinal));
            Assert.IsFalse(await earlierMoveNext);

            await using var replacement = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
            var replacementMoveNext = replacement.MoveNextAsync().AsTask();
            await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
                (start3, "C", new UsnJournalCursor(7UL, 510L), JournalEntryFactory.Create(1, 501, "fresh.txt")));

            Assert.IsTrue(await replacementMoveNext);
            Assert.AreEqual(510L, replacement.Current.Cursor.NextUsn);
        }
        finally
        {
            await client.DisposeAsync();
            _ = await earlierMoveNext;
            await earlier.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SendStartWatchAsync_ForADriveWhoseChannelWasFaulted_GivesTheNextSubscriberALiveChannel()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        var initialStart = await ReadOneFrameAsync(serverSide, cancellation.Token);

        var batchSource = client.CreateBatchSource();
        await using var faulted = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        var faultedMoveNext = faulted.MoveNextAsync().AsTask();
        await WriteAsync(serverSide, response => BrokerProtocol.WriteError(response, "C",
                WatchSpecArmEpochs.ForDrive(initialStart, "C"), "journal wrapped"),
            cancellation.Token);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            _ = await faultedMoveNext;
        });

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 500L) },
            cancellation.Token);
        var replacementStart = await ReadOneFrameAsync(serverSide, cancellation.Token);
        await using var replacement = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        var replacementMoveNext = replacement.MoveNextAsync().AsTask();
        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (replacementStart, "C", new UsnJournalCursor(7UL, 510L), JournalEntryFactory.Create(1, 501, "fresh.txt")));

        Assert.IsTrue(await replacementMoveNext);
        Assert.AreEqual(510L, replacement.Current.Cursor.NextUsn);
    }

    [TestMethod]
    public async Task SendDisarmDriveAsync_CompletesOnlyThatDrivesChannelAndWritesTheFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendStartWatchAsync(WatchCursors("C", "D"), cancellation.Token);
        var startWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);

        var batchSource = client.CreateBatchSource();
        await using var driveCEnumerator = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveCMoveNext = driveCEnumerator.MoveNextAsync().AsTask();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();

        await client.SendDisarmDriveAsync("C:\\", cancellation.Token);
        var disarm = await ReadOneFrameAsync(serverSide, cancellation.Token);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, disarm.Kind);
        Assert.AreEqual("C", disarm.RequireDrive());
        Assert.IsFalse(await driveCMoveNext);

        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (startWatch, "D", new UsnJournalCursor(7UL, 210L), JournalEntryFactory.Create(2, 201, "d.txt")));
        Assert.IsTrue(await driveDMoveNext);
        Assert.AreEqual(210L, driveDEnumerator.Current.Cursor.NextUsn);
    }

    [TestMethod]
    public async Task SendDisarmDriveAsync_WithNoWatchRunning_ThrowsInvalidOperationException()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        using var gatedClientSide = new GateFrameWriteStream(clientSide, BrokerFrameKind.DisarmDrive);
        await using var client = MakeMinimalFakeClient(gatedClientSide);

        try
        {
            _ = client.SendDisarmDriveAsync("C");
            Assert.Fail("Expected an InvalidOperationException.");
        }
        catch (InvalidOperationException exception)
        {
            StringAssert.Contains(exception.Message, "No live watch");
        }
        Assert.IsFalse(gatedClientSide.Entered.IsCompleted, "No frame should be written when no watch is running.");

        gatedClientSide.Release();
    }

    [TestMethod]
    public async Task Demux_DropsAJournalBatchForADriveThatIsNotArmed()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        var initialStart = await ReadOneFrameAsync(serverSide, cancellation.Token);
        await client.SendDisarmDriveAsync("C", cancellation.Token);
        var disarm = await ReadOneFrameAsync(serverSide, cancellation.Token);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, disarm.Kind);

        var batchSource = client.CreateBatchSource();
        await using var driveCEnumerator = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        var driveCMoveNext = driveCEnumerator.MoveNextAsync().AsTask();

        await client.SendStartWatchAsync(WatchCursors("D"), cancellation.Token);
        var driveDStart = await ReadOneFrameAsync(serverSide, cancellation.Token);
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();

        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (initialStart, "C", new UsnJournalCursor(7UL, 110L), JournalEntryFactory.Create(1, 101, "stale.txt")),
            (driveDStart, "D", new UsnJournalCursor(7UL, 210L), JournalEntryFactory.Create(2, 201, "d.txt")));

        Assert.IsTrue(await driveDMoveNext);
        Assert.AreEqual(210L, driveDEnumerator.Current.Cursor.NextUsn);
        Assert.IsFalse(await driveCMoveNext);
    }

    [TestMethod]
    public async Task Demux_ErrorFrameDisarmsThatDriveSoALaterBatchForItIsDropped()
    {
        var originalLogDirectory = BrokerDiagnostics.LogDirectory;
        var diagnosticsDirectory = Path.Combine(Path.GetTempPath(), "BrokerPerDriveArmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(diagnosticsDirectory);
        JournalBrokerClient? client = null;
        try
        {
            BrokerDiagnostics.LogDirectory = diagnosticsDirectory;
            BrokerDiagnostics.Enable("client-test");
            var (clientSide, serverSide) = DuplexStream.CreatePair();
            client = MakeMinimalFakeClient(clientSide);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.SendStartWatchAsync(WatchCursors("C", "D"), cancellation.Token);
            var startWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);

            var batchSource = client.CreateBatchSource();
            await using var driveCEnumerator = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
            await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
            var driveCMoveNext = driveCEnumerator.MoveNextAsync().AsTask();
            var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();

            await WriteAsync(serverSide, response =>
            {
                BrokerProtocol.WriteError(response, "C", WatchSpecArmEpochs.ForDrive(startWatch, "C"), "journal wrapped");
                BrokerProtocol.WriteJournalBatch(response, "C", WatchSpecArmEpochs.ForDrive(startWatch, "C"), new UsnJournalCursor(7UL, 110L),
                    [JournalEntryFactory.Create(1, 101, "stale.txt")]);
                BrokerProtocol.WriteJournalBatch(response, "D", WatchSpecArmEpochs.ForDrive(startWatch, "D"), new UsnJournalCursor(7UL, 210L),
                    [JournalEntryFactory.Create(2, 201, "d.txt")]);
                BrokerProtocol.WriteEndWatchAck(response);
            }, cancellation.Token);

            var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            {
                _ = await driveCMoveNext;
            });
            Assert.AreEqual("journal wrapped", exception.Message);
            Assert.IsTrue(await driveDMoveNext);

            // Disposing here (rather than only in the outer finally) is load-bearing: it
            // awaits the demux task's own completion, so no more background writes to
            // broker-diagnostics.log race the read below.
            await client.DisposeAsync();
            var diagnostics = await File.ReadAllTextAsync(
                Path.Combine(diagnosticsDirectory, "broker-diagnostics.log"), cancellation.Token);
            StringAssert.Contains(diagnostics, "Dropped a JournalBatch for drive C at arm epoch ");
        }
        finally
        {
            // DisposeAsync is idempotent; this is the safety net for the case where an
            // assertion above throws before the explicit dispose is reached.
            if (client != null)
            {
                await client.DisposeAsync();
            }

            BrokerDiagnostics.LogDirectory = originalLogDirectory;
            BrokerDiagnostics.ResetToDefaults();
            Directory.Delete(diagnosticsDirectory, true);
        }
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_ClearsEveryArmedDriveSoTheNextGenerationStartsClean()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendStartWatchAsync(WatchCursors("C", "D"), cancellation.Token);
        var initialStart = await ReadOneFrameAsync(serverSide, cancellation.Token);

        var stopTask = client.StopLiveWatchAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadOneFrameAsync(serverSide, cancellation.Token)).Kind);
        await WriteAsync(serverSide, BrokerProtocol.WriteEndWatchAck, cancellation.Token);
        await stopTask;

        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        var replacementStart = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var batchSource = client.CreateBatchSource();
        await using var driveCEnumerator = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveCMoveNext = driveCEnumerator.MoveNextAsync().AsTask();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();

        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (initialStart, "D", new UsnJournalCursor(7UL, 210L), JournalEntryFactory.Create(2, 201, "stale.txt")),
            (replacementStart, "C", new UsnJournalCursor(7UL, 110L), JournalEntryFactory.Create(1, 101, "c.txt")));

        Assert.IsTrue(await driveCMoveNext);
        Assert.AreEqual(110L, driveCEnumerator.Current.Cursor.NextUsn);
        Assert.IsFalse(await driveDMoveNext);
    }

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe,
            (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));
    }

    static Dictionary<string, UsnJournalCursor> WatchCursors(params string[] drives)
    {
        return drives.ToDictionary(drive => drive, _ => new UsnJournalCursor(7UL, 100L),
            StringComparer.OrdinalIgnoreCase);
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength), cancellationToken);
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    static Task WriteBatchesAndAckAsync(Stream stream, CancellationToken cancellationToken,
        params (BrokerFrame StartWatch, string Drive, UsnJournalCursor Cursor, UsnJournalEntry Entry)[] batches)
    {
        return WriteAsync(stream, response =>
        {
            foreach (var batch in batches)
            {
                BrokerProtocol.WriteJournalBatch(response, batch.Drive,
                    WatchSpecArmEpochs.ForDrive(batch.StartWatch, batch.Drive), batch.Cursor, [batch.Entry]);
            }

            BrokerProtocol.WriteEndWatchAck(response);
        }, cancellationToken);
    }

    static async Task WriteAsync(Stream stream, Action<ArrayBufferWriter<byte>> write,
        CancellationToken cancellationToken)
    {
        var response = new ArrayBufferWriter<byte>();
        write(response);
        await stream.WriteAsync(response.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
