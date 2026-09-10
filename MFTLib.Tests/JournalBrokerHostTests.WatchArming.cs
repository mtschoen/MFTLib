using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_ForADriveThatIsNotArmed_AddsItToTheLiveGenerationAndLeavesTheOthersRunning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, since, cancellationToken) =>
            {
                var invocation = new WatchInvocation(drive, since, cancellationToken);
                invocation.QueueBatch(new UsnJournalCursor(since.JournalId, since.NextUsn + 10));
                lock (invocations)
                {
                    invocations.Add(invocation);
                }

                return invocation.ReadBatchesAsync(cancellationToken);
            });

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        Assert.AreEqual("C", (await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token)).Drive);

        await WriteStartWatchAsync(clientSide, "D:1:0:2", cancellationSource.Token);
        await WaitForInvocationCountAsync(invocations, 2, cancellationSource.Token);
        await WriteBrokerRequestAsync(clientSide, BrokerProtocol.WriteEndWatch, cancellationSource.Token);
        var secondBatch = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, secondBatch.Kind);
        Assert.AreEqual("D", secondBatch.Drive);

        var acknowledgement = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, acknowledgement.Kind);
        CollectionAssert.AreEqual(new[] { "C", "D" }, SnapshotInvocations(invocations).Select(item => item.Drive).ToArray());

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_ForAnArmedDrive_StopsItsTaskAndRestartsItFromTheSuppliedCursor()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var firstStoppedBeforeReplacementStarted = false;
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, since, cancellationToken) =>
            {
                var invocation = new WatchInvocation(drive, since, cancellationToken);
                lock (invocations)
                {
                    if (since.NextUsn == 500)
                    {
                        firstStoppedBeforeReplacementStarted = invocations.Single().Stopped.Task.IsCompleted;
                    }

                    invocations.Add(invocation);
                }

                if (since.NextUsn == 100)
                {
                    invocation.QueueBatch(new UsnJournalCursor(7UL, 110L));
                }

                return invocation.ReadBatchesAsync(cancellationToken);
            });

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch,
            (await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token)).Kind);
        var firstInvocation = SnapshotInvocations(invocations).Single();

        await WriteStartWatchAsync(clientSide, "C:7:500:2", cancellationSource.Token);
        var recordedInvocations = await WaitForInvocationCountAsync(invocations, 2, cancellationSource.Token);
        Assert.IsTrue(firstInvocation.Stopped.Task.IsCompleted);
        Assert.IsTrue(firstStoppedBeforeReplacementStarted,
            "The prior watch must stop before the replacement watch source is invoked.");
        CollectionAssert.AreEqual(
            new[] { new UsnJournalCursor(7UL, 100L), new UsnJournalCursor(7UL, 500L) },
            recordedInvocations.Select(item => item.Since).ToArray());

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task DisarmDrive_StopsOnlyThatDrivesTaskAndTheOtherDriveKeepsStreaming()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateRecordingWatchHost(invocations);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:1,D:2:200:2", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        var startedInvocations = await WaitForInvocationCountAsync(invocations, 2, cancellationSource.Token);
        var driveC = startedInvocations.Single(item => item.Drive == "C");
        var driveD = startedInvocations.Single(item => item.Drive == "D");

        await WriteDisarmDriveAsync(clientSide, "C", cancellationSource.Token);
        await driveC.Stopped.Task.WaitAsync(cancellationSource.Token);
        Assert.IsFalse(driveD.CancellationToken.IsCancellationRequested);

        driveD.QueueBatch(new UsnJournalCursor(2UL, 210L));
        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("D", batch.Drive);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task DisarmDrive_ForADriveThatWasNeverArmed_IsIgnoredAndTheSessionKeepsServing()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) =>
                FakeWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))], cancellationToken));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        await WriteDisarmDriveAsync(clientSide, "D", cancellationSource.Token);
        var acknowledgement = await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("C", batch.Drive);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, acknowledgement.Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task EndWatch_AfterAPerDriveDisarm_StopsEveryRemainingDriveAndStillAcks()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateRecordingWatchHost(invocations);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:1,D:2:200:2", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        var startedInvocations = await WaitForInvocationCountAsync(invocations, 2, cancellationSource.Token);
        var driveC = startedInvocations.Single(item => item.Drive == "C");
        var driveD = startedInvocations.Single(item => item.Drive == "D");

        await WriteDisarmDriveAsync(clientSide, "C", cancellationSource.Token);
        await driveC.Stopped.Task.WaitAsync(cancellationSource.Token);
        var acknowledgement = await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token);
        await driveD.Stopped.Task.WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, acknowledgement.Kind);

        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
        await serverSide.DisposeAsync();
        Assert.AreEqual(0, ReadAllFrames(clientSide).Count(frame => frame.Kind == BrokerFrameKind.EndWatchAck));
    }

    static JournalBrokerHost CreateRecordingWatchHost(List<WatchInvocation> invocations)
    {
        return CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, since, cancellationToken) =>
            {
                var invocation = new WatchInvocation(drive, since, cancellationToken);
                lock (invocations)
                {
                    invocations.Add(invocation);
                }

                return invocation.ReadBatchesAsync(cancellationToken);
            });
    }

    static async Task<WatchInvocation[]> WaitForInvocationCountAsync(
        List<WatchInvocation> invocations, int expectedCount, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = SnapshotInvocations(invocations);
            if (snapshot.Length == expectedCount)
            {
                await Task.WhenAll(snapshot.Select(item => item.Started.Task)).WaitAsync(cancellationToken);
                return snapshot;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    static WatchInvocation[] SnapshotInvocations(List<WatchInvocation> invocations)
    {
        lock (invocations)
        {
            return invocations.ToArray();
        }
    }

    static Task WriteStartWatchAsync(Stream stream, string watchSpec, CancellationToken cancellationToken)
    {
        return WriteBrokerRequestAsync(stream, writer => BrokerProtocol.WriteStartWatch(writer, watchSpec),
            cancellationToken);
    }

    static Task WriteDisarmDriveAsync(Stream stream, string drive, CancellationToken cancellationToken)
    {
        return WriteBrokerRequestAsync(stream, writer => BrokerProtocol.WriteDisarmDrive(writer, drive),
            cancellationToken);
    }

    static async Task<BrokerFrame> EndWatchAndReadAcknowledgementAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        await WriteBrokerRequestAsync(stream, BrokerProtocol.WriteEndWatch, cancellationToken);
        return await ReadOneFrameAsync(stream).WaitAsync(cancellationToken);
    }

    static async Task<List<BrokerFrame>> EndWatchAndCollectFramesAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        await WriteBrokerRequestAsync(stream, BrokerProtocol.WriteEndWatch, cancellationToken);
        var frames = new List<BrokerFrame>();
        do
        {
            frames.Add(await ReadOneFrameAsync(stream).WaitAsync(cancellationToken));
        }
        while (frames[^1].Kind != BrokerFrameKind.EndWatchAck);

        return frames;
    }

    static Task ShutdownAsync(Stream stream, CancellationToken cancellationToken)
    {
        return WriteBrokerRequestAsync(stream, BrokerProtocol.WriteShutdown, cancellationToken);
    }

    static async Task WriteBrokerRequestAsync(
        Stream stream, Action<ArrayBufferWriter<byte>> writeFrame, CancellationToken cancellationToken)
    {
        var request = new ArrayBufferWriter<byte>();
        writeFrame(request);
        await stream.WriteAsync(request.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    sealed class WatchInvocation(
        string drive, UsnJournalCursor since, CancellationToken cancellationToken)
    {
        readonly Channel<(UsnJournalEntry[], UsnJournalCursor)> _batches = Channel.CreateUnbounded<
            (UsnJournalEntry[], UsnJournalCursor)>();

        public string Drive { get; } = drive;

        public UsnJournalCursor Since { get; } = since;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void QueueBatch(UsnJournalCursor cursor)
        {
            Assert.IsTrue(_batches.Writer.TryWrite(([SampleEntry()], cursor)));
        }

        public async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                CancellationToken, cancellationToken);
            try
            {
                await foreach (var batch in _batches.Reader.ReadAllAsync(linkedCancellation.Token))
                {
                    yield return batch;
                }
            }
            finally
            {
                Stopped.TrySetResult();
            }
        }
    }
}
