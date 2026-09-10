using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    [TestMethod]
    public void WatchSpecArmEpochs_ForDrive_MatchesDriveLetterCaseInsensitively()
    {
        var startWatch = BrokerFrame.StartWatch("C:7:100:4");

        Assert.AreEqual(4U, WatchSpecArmEpochs.ForDrive(startWatch, "c"));
    }

    [TestMethod]
    public void WatchSpecArmEpochs_ForDrive_MissingDrive_ThrowsWithDriveAndSpec()
    {
        var startWatch = BrokerFrame.StartWatch("C:7:100:4");

        var exception = Assert.ThrowsException<AssertFailedException>(() =>
            WatchSpecArmEpochs.ForDrive(startWatch, "D"));

        StringAssert.Contains(exception.Message, "D");
        StringAssert.Contains(exception.Message, "C:7:100:4");
    }

    [TestMethod]
    public async Task SendStartWatchAsync_CalledTwiceWithoutStop_ArmsTheSecondCallsDrives()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedFrames = new List<BrokerFrame>();
        var serverTask = RespondToStartFramesAsync(serverSide, receivedFrames, cancellation.Token);

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, cancellation.Token);
        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["D"] = new(9UL, 200L) }, cancellation.Token);
        await serverTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, receivedFrames[0].Kind);
        var firstSpec = receivedFrames[0].DrivesSpec;
        Assert.IsNotNull(firstSpec);
        Assert.IsTrue(firstSpec.StartsWith("C:7:100:", StringComparison.Ordinal));
        Assert.AreEqual(BrokerFrameKind.StartWatch, receivedFrames[1].Kind);
        var secondSpec = receivedFrames[1].DrivesSpec;
        Assert.IsNotNull(secondSpec);
        Assert.IsTrue(secondSpec.StartsWith("D:9:200:", StringComparison.Ordinal));

        static async Task RespondToStartFramesAsync(Stream serverStream, ICollection<BrokerFrame> frames,
            CancellationToken cancellationToken)
        {
            frames.Add(await ReadOneFrameAsync(serverStream).WaitAsync(cancellationToken));
            frames.Add(await ReadOneFrameAsync(serverStream).WaitAsync(cancellationToken));
            var acknowledgement = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteEndWatchAck(acknowledgement);
            await serverStream.WriteAsync(acknowledgement.WrittenMemory, cancellationToken);
            await serverStream.FlushAsync(cancellationToken);
        }
    }

    [TestMethod]
    public async Task CreateBatchSource_ChannelCompletesCleanly_EnumerationEndsWithoutError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        var enumerateTask = Task.Run(async () =>
        {
            await foreach (var batch in batchSource("C:\\", default, CancellationToken.None))
            {
                received.Add(batch);
            }
        });

        await ReadOneFrameAsync(serverSide); // consume the StartWatch request

        var acknowledgement = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(acknowledgement);
        await serverSide.WriteAsync(acknowledgement.WrittenMemory);
        await serverSide.FlushAsync();

        await enumerateTask; // must complete normally - no exception
        Assert.AreEqual(0, received.Count);
    }

    [TestMethod]
    public async Task CreateBatchSource_DemuxReadThrows_SignalsBrokerDeathWithExceptionMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);

        string? deathMessage = null;
        client.BrokerDied += message => deathMessage = message;

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await serverSide.WriteAsync(header);
        await serverSide.WriteAsync(new byte[] { 1, 2, 3 });
        await serverSide.FlushAsync();
        await serverSide.DisposeAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, CancellationToken.None))
            {
            }
        });

        Assert.IsNotNull(deathMessage);
        StringAssert.Contains(deathMessage, "Truncated broker frame");
    }

    [TestMethod]
    public async Task CreateBatchSource_CancelledBetweenFrames_ChannelCompletesCleanly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var cancellation = new CancellationTokenSource();
        Action cancel = cancellation.Cancel;
        using var wrapped = new CancelAfterReadsStream(clientSide, 2, cancel);
        await using var client = new JournalBrokerClient(wrapped,
            (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) }, cancellation.Token);

        var startWatch = await ReadOneFrameAsync(serverSide);
        var entry = JournalEntryFactory.Create(1, 10, "a");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", WatchSpecArmEpochs.ForDrive(startWatch, "C"),
            new UsnJournalCursor(7UL, 110L), [entry]);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        var batchSource = client.CreateBatchSource();
        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in batchSource("C:\\", default, timeout.Token))
        {
            received.Add(batch);
        }

        Assert.AreEqual(1, received.Count);

        cancellation.Dispose();
    }

    [TestMethod]
    public async Task CreateBatchSource_CancelledBetweenFrames_CompletesAllLiveChannels()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var cancellation = new CancellationTokenSource();
        Action cancel = cancellation.Cancel;
        using var wrapped = new CancelAfterReadsStream(clientSide, 4, cancel);
        await using var client = new JournalBrokerClient(wrapped,
            (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));

        await client.SendStartWatchAsync(
            new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L), ["D"] = new(7UL, 200L) }, cancellation.Token);

        var startWatch = await ReadOneFrameAsync(serverSide);
        var entryC = JournalEntryFactory.Create(1, 10, "a");
        var entryD = JournalEntryFactory.Create(2, 20, "b");
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(response, "C", WatchSpecArmEpochs.ForDrive(startWatch, "C"),
            new UsnJournalCursor(7UL, 110L), [entryC]);
        BrokerProtocol.WriteJournalBatch(response, "D", WatchSpecArmEpochs.ForDrive(startWatch, "D"),
            new UsnJournalCursor(9UL, 210L), [entryD]);
        await serverSide.WriteAsync(response.WrittenMemory, CancellationToken.None);
        await serverSide.FlushAsync(CancellationToken.None);

        var batchSource = client.CreateBatchSource();
        var receivedC = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        var receivedD = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var batch in batchSource("C:\\", default, timeout.Token))
        {
            receivedC.Add(batch);
        }

        await foreach (var batch in batchSource("D:\\", default, timeout.Token))
        {
            receivedD.Add(batch);
        }

        Assert.AreEqual(1, receivedC.Count);
        Assert.AreEqual(1, receivedD.Count);

        cancellation.Dispose();
    }
}
