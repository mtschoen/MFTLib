using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerPerDriveArmTests
{
    [TestMethod]
    public async Task SendStartWatchAsync_WhenFirstWriteFailsAfterTransmissionStarts_LaterStartCreatesWorkingDemux()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var failingClientSide = new FailFirstFrameWriteStream(clientSide, BrokerFrameKind.StartWatch);
        await using var client = MakeMinimalFakeClient(failingClientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstTransmissionStarted = false;

        await Assert.ThrowsExceptionAsync<IOException>(() => client.SendStartWatchAsync(
            WatchCursors("C"), () => firstTransmissionStarted = true, cancellation.Token));
        Assert.IsTrue(firstTransmissionStarted, "The failed write must occur after the transmission callback.");

        await client.SendStartWatchAsync(WatchCursors("D"), cancellation.Token);
        var start1 = await ReadOneFrameAsync(serverSide, cancellation.Token);
        Assert.IsNotNull(start1.DrivesSpec);
        Assert.IsTrue(start1.DrivesSpec.StartsWith("D:7:100:", StringComparison.Ordinal));

        var batchSource = client.CreateBatchSource();
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();
        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (start1, "D", new UsnJournalCursor(7UL, 210L), JournalEntryFactory.Create(2, 201, "d.txt")));

        Assert.IsTrue(await driveDMoveNext);
        Assert.AreEqual(210L, driveDEnumerator.Current.Cursor.NextUsn);
    }

    [TestMethod]
    public async Task SendStartWatchAsync_WhenConcurrentClaimantIsCancelledBeforeTransmission_OtherStartCreatesWorkingDemux()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var firstCancellation = new CancellationTokenSource();
        var writeLock = GetPrivateField<SemaphoreSlim>(client, "_writeLock");
        await writeLock.WaitAsync(cancellation.Token);
        var firstTransmissionStarted = false;
        var secondTransmissionStarted = false;
        Task firstStart;
        Task secondStart;
        try
        {
            firstStart = client.SendStartWatchAsync(
                WatchCursors("C"), () => firstTransmissionStarted = true, firstCancellation.Token);
            secondStart = client.SendStartWatchAsync(
                WatchCursors("D"), () => secondTransmissionStarted = true, cancellation.Token);
            firstCancellation.Cancel();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => firstStart);
        }
        finally
        {
            writeLock.Release();
        }

        await secondStart;
        Assert.IsFalse(firstTransmissionStarted, "The cancelled claimant must not reach transmission.");
        Assert.IsTrue(secondTransmissionStarted, "The surviving caller must transmit its frame.");
        var start1 = await ReadOneFrameAsync(serverSide, cancellation.Token);
        Assert.IsNotNull(start1.DrivesSpec);
        Assert.IsTrue(start1.DrivesSpec.StartsWith("D:7:100:", StringComparison.Ordinal));

        var batchSource = client.CreateBatchSource();
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();
        await WriteBatchesAndAckAsync(serverSide, cancellation.Token,
            (start1, "D", new UsnJournalCursor(7UL, 210L), JournalEntryFactory.Create(2, 201, "d.txt")));

        Assert.IsTrue(await driveDMoveNext);
    }

    [TestMethod]
    public async Task StopLiveWatchAsync_WithLeakedClaimAndNoDemux_AllowsFreshGenerationAndDropsStaleDriveBatch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        GetPrivateField<Dictionary<string, uint>>(client, "_armedEpochsByDrive").Add("D", 99);
        SetPrivateField(client, "_liveWatchGenerationStarted", true);

        await client.StopLiveWatchAsync();
        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        var start2 = await ReadOneFrameAsync(serverSide, cancellation.Token);
        Assert.IsNotNull(start2.DrivesSpec);
        Assert.IsTrue(start2.DrivesSpec.StartsWith("C:7:100:", StringComparison.Ordinal));

        var batchSource = client.CreateBatchSource();
        await using var driveCEnumerator = batchSource("C", default, cancellation.Token).GetAsyncEnumerator();
        await using var driveDEnumerator = batchSource("D", default, cancellation.Token).GetAsyncEnumerator();
        var driveCMoveNext = driveCEnumerator.MoveNextAsync().AsTask();
        var driveDMoveNext = driveDEnumerator.MoveNextAsync().AsTask();
        await WriteAsync(serverSide, response =>
        {
            BrokerProtocol.WriteJournalBatch(response, "D", BrokerFrame.NoArmEpoch,
                new UsnJournalCursor(7UL, 210L), [JournalEntryFactory.Create(2, 201, "stale.txt")]);
            BrokerProtocol.WriteJournalBatch(response, "C", WatchSpecArmEpochs.ForDrive(start2, "C"),
                new UsnJournalCursor(7UL, 110L), [JournalEntryFactory.Create(1, 101, "fresh.txt")]);
            BrokerProtocol.WriteEndWatchAck(response);
        }, cancellation.Token);

        await Task.WhenAll(driveCMoveNext, driveDMoveNext);

        Assert.IsTrue(await driveCMoveNext);
        Assert.AreEqual(110L, driveCEnumerator.Current.Cursor.NextUsn);
        Assert.IsFalse(await driveDMoveNext);
    }

    [TestMethod]
    public async Task SendStartWatchAsync_AfterDemuxEndedWithoutStop_ThrowsInvalidOperationException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = MakeMinimalFakeClient(clientSide);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.SendStartWatchAsync(WatchCursors("C"), cancellation.Token);
        await ReadOneFrameAsync(serverSide, cancellation.Token);

        await WriteAsync(serverSide, BrokerProtocol.WriteEndWatchAck, cancellation.Token);
        await GetPrivateField<Task>(client, "_demuxTask").WaitAsync(cancellation.Token);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            client.SendStartWatchAsync(WatchCursors("D"), cancellation.Token));
    }

    static T GetPrivateField<T>(JournalBrokerClient client, string fieldName)
    {
        return (T)typeof(JournalBrokerClient)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
    }

    static void SetPrivateField(JournalBrokerClient client, string fieldName, object value)
    {
        typeof(JournalBrokerClient)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(client, value);
    }

    sealed class FailFirstFrameWriteStream(Stream inner, BrokerFrameKind frameKind) : Stream
    {
        int _failed;

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 5 && buffer.Span[4] == (byte)frameKind
                                   && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException("Scripted failure after transmission started.");
            }

            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
