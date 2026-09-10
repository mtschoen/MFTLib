using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Threading.Channels;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class BrokerArmOrderingTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task SendStartWatchAsync_IssuesEpochOneForTheFirstArmSoZeroIsNeverALiveEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C", 7UL, 100L), cancellation.Token);
        var startWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);

        Assert.AreEqual("C:7:100:1", startWatch.DrivesSpec);
        Assert.AreEqual(1U, WatchSpecArmEpochs.ForDrive(startWatch, "C"));
    }

    [TestMethod]
    public async Task SendStartWatchAsync_IssuesAHigherEpochForEveryLaterArmOfTheSameDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C", 7UL, 100L), cancellation.Token);
        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        await client.SendStartWatchAsync(WatchCursor("C", 7UL, 200L), cancellation.Token);
        var secondStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        await client.SendStartWatchAsync(WatchCursor("C", 7UL, 300L), cancellation.Token);
        var thirdStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);

        var firstArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        var secondArmEpoch = WatchSpecArmEpochs.ForDrive(secondStartWatch, "C");
        var thirdArmEpoch = WatchSpecArmEpochs.ForDrive(thirdStartWatch, "C");
        Assert.IsTrue(firstArmEpoch < secondArmEpoch);
        Assert.IsTrue(secondArmEpoch < thirdArmEpoch);
    }

    [TestMethod]
    public async Task SendStartWatchAsync_IssuesDistinctEpochsAcrossDrivesInOneCall()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cursorsByDrive = new Dictionary<string, UsnJournalCursor>(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = new(7UL, 100L),
            ["D"] = new(9UL, 200L)
        };

        await client.SendStartWatchAsync(cursorsByDrive, cancellation.Token);
        var startWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var driveCArmEpoch = WatchSpecArmEpochs.ForDrive(startWatch, "C");
        var driveDArmEpoch = WatchSpecArmEpochs.ForDrive(startWatch, "D");

        Assert.AreNotEqual(BrokerFrame.NoArmEpoch, driveCArmEpoch);
        Assert.AreNotEqual(BrokerFrame.NoArmEpoch, driveDArmEpoch);
        Assert.AreNotEqual(driveCArmEpoch, driveDArmEpoch);
    }

    [TestMethod]
    public async Task SendStartWatchAsync_EndedGenerationValidationLeavesEarlierArmsIntact()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cursorsByDrive = new Dictionary<string, UsnJournalCursor>
        {
            ["C"] = new(7UL, 100L),
            ["D"] = new(9UL, 200L)
        };

        await client.SendStartWatchAsync(cursorsByDrive, cancellation.Token);
        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var driveCArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        var driveDArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "D");
        var liveEndedField = typeof(JournalBrokerClient)
            .GetField("_liveEnded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var liveChannelsLock = GetPrivateField<object>(client, "_liveChannelsLock");

        lock (liveChannelsLock)
        {
            liveEndedField.SetValue(client, true);
        }
        try
        {
            var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                client.SendStartWatchAsync(cursorsByDrive, cancellation.Token));
            StringAssert.Contains(exception.Message, "Call StopLiveWatchAsync before starting again");

            lock (liveChannelsLock)
            {
                var armedEpochsByDrive = GetPrivateField<Dictionary<string, uint>>(client, "_armedEpochsByDrive");
                Assert.AreEqual(driveCArmEpoch, armedEpochsByDrive["C"]);
                Assert.AreEqual(driveDArmEpoch, armedEpochsByDrive["D"]);
            }
        }
        finally
        {
            lock (liveChannelsLock)
            {
                liveEndedField.SetValue(client, false);
            }
        }
    }

    [TestMethod]
    public async Task SendStartWatchAsync_AfterAStopThatTimedOut_NeverReissuesAnEarlierEpoch()
    {
        var previousTimeout = JournalBrokerClient._endWatchAckTimeout;
        JournalBrokerClient._endWatchAckTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            var (clientSide, serverSide) = DuplexStream.CreatePair();
            await using var client = MakeMinimalFakeClient(clientSide);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await client.SendStartWatchAsync(WatchCursor("C", 7UL, 100L), cancellation.Token);
            var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
            var firstArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");

            await client.StopLiveWatchAsync().WaitAsync(cancellation.Token);
            Assert.IsTrue(client.LastStopTimedOut);
            Assert.AreEqual(BrokerFrameKind.EndWatch,
                (await ReadOneFrameAsync(serverSide, cancellation.Token)).Kind);

            await client.SendStartWatchAsync(WatchCursor("C", 7UL, 500L), cancellation.Token);
            var secondStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
            var secondArmEpoch = WatchSpecArmEpochs.ForDrive(secondStartWatch, "C");

            Assert.IsTrue(secondArmEpoch > firstArmEpoch);
        }
        finally
        {
            JournalBrokerClient._endWatchAckTimeout = previousTimeout;
        }
    }

    [TestMethod]
    public async Task SendStartWatchAsync_ConcurrentArmsOfOneDrive_PutTheHigherEpochLastOnTheWire()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gatedClientSide = new GateFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        await using var client = MakeMinimalFakeClient(gatedClientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var firstArm = client.SendStartWatchAsync(WatchCursor("C", 7UL, 100L), cancellation.Token);
        Task secondArm;
        try
        {
            await gatedClientSide.Entered.WaitAsync(cancellation.Token);
            secondArm = client.SendStartWatchAsync(WatchCursor("C", 7UL, 500L), cancellation.Token);
        }
        finally
        {
            gatedClientSide.Release();
        }

        await Task.WhenAll(firstArm, secondArm).WaitAsync(cancellation.Token);

        var firstStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var secondStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        var firstArmEpoch = WatchSpecArmEpochs.ForDrive(firstStartWatch, "C");
        var secondArmEpoch = WatchSpecArmEpochs.ForDrive(secondStartWatch, "C");
        Assert.IsTrue(firstArmEpoch < secondArmEpoch);

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
        Assert.AreEqual("fresh.txt", batches.Current.Entries.Single().FileName);
    }

    [TestMethod]
    public async Task SendDisarmDriveAsync_WhileAnArmHoldsTheOrderingGate_ReachesTheWireAfterThatArm()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var gatedClientSide = new GateFrameWriteStream(
            clientSide, BrokerFrameKind.StartWatch, occurrence: 2);
        await using var client = MakeMinimalFakeClient(gatedClientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.SendStartWatchAsync(WatchCursor("C", 7UL, 100L), cancellation.Token);
        var initialStartWatch = await ReadOneFrameAsync(serverSide, cancellation.Token);
        _ = WatchSpecArmEpochs.ForDrive(initialStartWatch, "C");

        var rearm = client.SendStartWatchAsync(WatchCursor("C", 7UL, 500L), cancellation.Token);
        IAsyncEnumerator<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> batches;
        Task<bool> moveNext;
        Task disarm;
        bool subscriberCompletedBeforeRelease;
        try
        {
            await gatedClientSide.Entered.WaitAsync(cancellation.Token);
            batches = client.CreateBatchSource()("C", default, cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            moveNext = batches.MoveNextAsync().AsTask();
            var liveChannels = GetPrivateField<
                Dictionary<string, Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>>>(
                    client, "_liveChannels");
            var replacementChannelCompletion = liveChannels["C"].Reader.Completion;

            disarm = client.SendDisarmDriveAsync("C", cancellation.Token);
            subscriberCompletedBeforeRelease = replacementChannelCompletion.IsCompleted;
        }
        finally
        {
            gatedClientSide.Release();
        }

        try
        {
            await Task.WhenAll(rearm, disarm).WaitAsync(cancellation.Token);

            var rearmFrame = await ReadOneFrameAsync(serverSide, cancellation.Token);
            var rearmEpoch = WatchSpecArmEpochs.ForDrive(rearmFrame, "C");
            var disarmFrame = await ReadOneFrameAsync(serverSide, cancellation.Token);
            Assert.AreEqual(BrokerFrameKind.StartWatch, rearmFrame.Kind);
            Assert.AreNotEqual(BrokerFrame.NoArmEpoch, rearmEpoch);
            Assert.AreEqual(BrokerFrameKind.DisarmDrive, disarmFrame.Kind);
            Assert.AreEqual("C", disarmFrame.RequireDrive());
            Assert.IsFalse(await moveNext.WaitAsync(cancellation.Token));
            Assert.IsFalse(subscriberCompletedBeforeRelease,
                "Disarm must not complete the replacement channel until the gated arm reaches the wire.");
        }
        finally
        {
            await batches.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SendDisarmDriveAsync_WithNoWatchRunning_StillThrowsWithoutAwaiting()
    {
        var (clientSide, _) = DuplexStream.CreatePair();
        using var gatedClientSide = new GateFrameWriteStream(clientSide, BrokerFrameKind.DisarmDrive);
        await using var client = MakeMinimalFakeClient(gatedClientSide);

        try
        {
            try
            {
                _ = client.SendDisarmDriveAsync("C");
                Assert.Fail("Expected SendDisarmDriveAsync to throw from the call itself.");
            }
            catch (InvalidOperationException exception)
            {
                StringAssert.Contains(exception.Message, "No live watch");
            }

            Assert.IsFalse(gatedClientSide.Entered.IsCompleted,
                "No DisarmDrive frame should reach the pipe when no watch is running.");
        }
        finally
        {
            gatedClientSide.Release();
        }
    }

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe,
            (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOperationDisposable.Instance));
    }

    static Dictionary<string, UsnJournalCursor> WatchCursor(string drive, ulong journalId, long nextUsn)
    {
        return new Dictionary<string, UsnJournalCursor>(StringComparer.OrdinalIgnoreCase)
        {
            [drive] = new(journalId, nextUsn)
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

    static TField GetPrivateField<TField>(object instance, string fieldName)
    {
        return (TField)instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;
    }

    sealed class NoOperationDisposable : IDisposable
    {
        public static readonly NoOperationDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
