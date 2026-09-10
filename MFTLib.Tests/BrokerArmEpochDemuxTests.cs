using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class BrokerArmEpochDemuxTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task Demux_DeliversAJournalBatchTaggedWithTheDrivesCurrentArmEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var startWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var armEpoch = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        await using var batches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        await WriteFrameAsync(serverSide, writer => BrokerProtocol.WriteJournalBatch(
            writer, "C", armEpoch, new UsnJournalCursor(7UL, 110L),
            [JournalEntryFactory.Create(1, 101L, "current.txt")]), cancellation.Token);

        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(cancellation.Token));
        Assert.AreEqual(110L, batches.Current.Cursor.NextUsn);
        Assert.AreEqual("current.txt", batches.Current.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task SendStartWatchAsync_InvalidDriveLeavesTheExistingArmDeliveringItsCurrentEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var existingArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        var invalidArm = new Dictionary<string, UsnJournalCursor>
        {
            ["C"] = new(7UL, 500L),
            ["not-a-drive"] = new(9UL, 200L)
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            client.SendStartWatchAsync(invalidArm, cancellation.Token));

        await using var batches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await WriteFrameAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteJournalBatch(writer, "C", existingArmEpoch,
                new UsnJournalCursor(7UL, 110L), [JournalEntryFactory.Create(1, 101L, "existing.txt")]);
            BrokerProtocol.WriteEndWatchAck(writer);
        }, cancellation.Token);

        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(cancellation.Token));
        Assert.AreEqual("existing.txt", batches.Current.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task Demux_DropsAJournalBatchTaggedWithASupersededArmEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var firstArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        await client.SendStartWatchAsync(WatchCursor("C", 500L), cancellation.Token);
        var secondStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var secondArmEpoch = WatchSpecArmEpochs.ForDrive(secondStartWatch, "C");
        await using var batches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        await WriteFrameAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteJournalBatch(writer, "C", firstArmEpoch, new UsnJournalCursor(7UL, 110L),
                [JournalEntryFactory.Create(1, 101L, "stale.txt")]);
            BrokerProtocol.WriteJournalBatch(writer, "C", secondArmEpoch, new UsnJournalCursor(7UL, 510L),
                [JournalEntryFactory.Create(2, 501L, "fresh.txt")]);
        }, cancellation.Token);

        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(cancellation.Token));
        Assert.AreEqual(510L, batches.Current.Cursor.NextUsn);
        Assert.AreEqual("fresh.txt", batches.Current.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task Demux_DropsAnErrorTaggedWithASupersededArmEpochAndKeepsTheFreshChannelStreaming()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var firstArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        await client.SendStartWatchAsync(WatchCursor("C", 500L), cancellation.Token);
        var secondStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var secondArmEpoch = WatchSpecArmEpochs.ForDrive(secondStartWatch, "C");
        await using var batches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        await WriteFrameAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteError(writer, "C", firstArmEpoch, "retired arm failed");
            BrokerProtocol.WriteJournalBatch(writer, "C", secondArmEpoch, new UsnJournalCursor(7UL, 510L),
                [JournalEntryFactory.Create(2, 501L, "fresh.txt")]);
        }, cancellation.Token);

        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(cancellation.Token));
        Assert.AreEqual("fresh.txt", batches.Current.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task Demux_DropsAFrameTaggedWithTheNoArmEpochSentinel()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var startWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var armEpoch = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        await using var batches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        await WriteFrameAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteJournalBatch(writer, "C", BrokerFrame.NoArmEpoch,
                new UsnJournalCursor(7UL, 105L),
                [JournalEntryFactory.Create(1, 101L, "scan-path.txt")]);
            BrokerProtocol.WriteJournalBatch(writer, "C", armEpoch, new UsnJournalCursor(7UL, 110L),
                [JournalEntryFactory.Create(2, 106L, "live.txt")]);
        }, cancellation.Token);

        Assert.IsTrue(await batches.MoveNextAsync().AsTask().WaitAsync(cancellation.Token));
        Assert.AreEqual(110L, batches.Current.Cursor.NextUsn);
        Assert.AreEqual("live.txt", batches.Current.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task Demux_DropsAJournalBatchForADriveThatIsNotArmedWhateverEpochItCarries()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var driveCStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var driveCArmEpoch = WatchSpecArmEpochs.ForDrive(driveCStartWatch, "C");
        await client.SendDisarmDriveAsync("C", cancellation.Token);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive,
            (await ReadOneFrameAsync(serverSide, cancellation.Token)).Kind);

        await client.SendStartWatchAsync(WatchCursor("D"), cancellation.Token);
        var driveDStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var driveDArmEpoch = WatchSpecArmEpochs.ForDrive(driveDStartWatch, "D");
        await using var driveDBatches = client.CreateBatchSource()("D", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        await WriteFrameAsync(serverSide, writer =>
        {
            BrokerProtocol.WriteJournalBatch(writer, "C", driveCArmEpoch, new UsnJournalCursor(7UL, 110L),
                [JournalEntryFactory.Create(1, 101L, "unarmed.txt")]);
            BrokerProtocol.WriteJournalBatch(writer, "D", driveDArmEpoch, new UsnJournalCursor(7UL, 210L),
                [JournalEntryFactory.Create(2, 201L, "armed.txt")]);
        }, cancellation.Token);

        Assert.IsTrue(await driveDBatches.MoveNextAsync().AsTask().WaitAsync(cancellation.Token));
        Assert.AreEqual("armed.txt", driveDBatches.Current.Entries.Single().FileName);

        await using var driveCBatches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var driveCMoveNext = driveCBatches.MoveNextAsync().AsTask();
        await WriteFrameAsync(serverSide, BrokerProtocol.WriteEndWatchAck, cancellation.Token);
        Assert.IsFalse(await driveCMoveNext.WaitAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task Demux_FaultsTheDriveAWarningNamesWhateverEpochIsCurrent()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C"), cancellation.Token);
        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        _ = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        await client.SendStartWatchAsync(WatchCursor("C", 500L), cancellation.Token);
        var secondStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        _ = WatchSpecArmEpochs.ForDrive(secondStartWatch, "C");
        await using var batches = client.CreateBatchSource()("C", default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var moveNext = batches.MoveNextAsync().AsTask();

        await WriteFrameAsync(serverSide,
            writer => BrokerProtocol.WriteWarning(writer, "C", "unexpected scan warning"), cancellation.Token);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await moveNext.WaitAsync(cancellation.Token));
        StringAssert.Contains(exception.Message, "Unexpected Warning frame");
        StringAssert.Contains(exception.Message, "drive C");
    }

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe,
            (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOperationDisposable.Instance));
    }

    static Dictionary<string, UsnJournalCursor> WatchCursor(string drive, long nextUsn = 100L)
    {
        return new Dictionary<string, UsnJournalCursor>(StringComparer.OrdinalIgnoreCase)
        {
            [drive] = new(7UL, nextUsn)
        };
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

    static async Task WriteFrameAsync(Stream stream, Action<ArrayBufferWriter<byte>> writeFrame,
        CancellationToken cancellationToken)
    {
        var response = new ArrayBufferWriter<byte>();
        writeFrame(response);
        await stream.WriteAsync(response.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    sealed class NoOperationDisposable : IDisposable
    {
        public static readonly NoOperationDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
