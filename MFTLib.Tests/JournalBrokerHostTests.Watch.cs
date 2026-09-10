using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_StreamsBatches_UntilCancelled()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var batches = new[]
        {
            (new[] { SampleEntry() }, new UsnJournalCursor(7UL, 110L)),
            ([SampleEntry()], new UsnJournalCursor(7UL, 120L))
        };
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(batches, cancellationToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        var second = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, second.Kind);
        Assert.AreEqual("C", first.Drive);
        Assert.AreEqual(new UsnJournalCursor(7UL, 110L), first.Cursor);
        Assert.AreEqual(new UsnJournalCursor(7UL, 120L), second.Cursor);

        await cts.CancelAsync();
        await serveTask; // returns cleanly once cancelled
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursor_QueriesCurrentCursorBeforeWatching()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        UsnJournalCursor watchedFrom = default;
        var host = CreateHost(
            _ => new UsnJournalCursor(9UL, 500L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, since, cancellationToken) =>
            {
                watchedFrom = since;
                return FakeWatch([([SampleEntry()], new UsnJournalCursor(9UL, 510L))],
                    cancellationToken);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:0:0:1"); // no cached cursor -> sentinel
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(new UsnJournalCursor(9UL, 500L), watchedFrom);

        await cts.CancelAsync();
        await serveTask;
    }

    [TestMethod]
    public async Task EndWatch_StopsWatchTasks_AndWritesEndWatchAck()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        // The watch source yields one batch then would block on Infinite until
        // cancelled. After EndWatch cancels it, no further batch can appear, and
        // the host must reply EndWatchAck.
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) =>
            {
                watchStarted.TrySetResult();
                return FakeWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))],
                    cancellationToken);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(startRequest, "C:7:100:1");
        await clientSide.WriteAsync(startRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        // Drain the single live batch, then ask the host to end the watch.
        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        await watchStarted.Task;

        var endRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatch(endRequest);
        await clientSide.WriteAsync(endRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        // The next frame the host writes must be the ack: the cancelled watch task
        // stopped yielding (FakeWatch was blocked on Infinite), so no further
        // JournalBatch can race ahead of the ack.
        var ack = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, ack.Kind);

        // The session stays alive after the ack; shut it down cleanly.
        var shutdownRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdownRequest);
        await clientSide.WriteAsync(shutdownRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task ServeAsync_TokenAlreadyCancelled_ReturnsImmediatelyWithoutReading()
    {
        var (_, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<MftRecord>(), Array.Empty<UsnJournalEntry>());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);
        var finished = await Task.WhenAny(serveTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.AreSame(serveTask, finished);
        await serveTask;
    }

    [TestMethod]
    public async Task ServeAsync_ClientClosesAfterOneRequest_ReturnsCleanlyOnEof()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<MftRecord>(), Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();
        await clientSide.DisposeAsync(); // close before Shutdown - the host's second read hits clean EOF

        // oneShot: false so ServeAsync loops back and must observe the EOF itself.
        await host.ServeAsync(serverSide, CreateSectionWriter(), false, CancellationToken.None);
    }

    [TestMethod]
    public async Task ServeAsync_TruncatedFrameBody_ThrowsEndOfStreamException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<MftRecord>(), Array.Empty<UsnJournalEntry>());

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10); // claims a 10-byte frame
        await clientSide.WriteAsync(header);
        await clientSide.WriteAsync(new byte[] { 1, 2, 3 }); // delivers only 3
        await clientSide.FlushAsync();
        await clientSide.DisposeAsync(); // EOF partway through the frame body

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            host.ServeAsync(serverSide, CreateSectionWriter(), false, CancellationToken.None));
    }

    [TestMethod]
    public async Task ServeAsync_HeaderOnlyThenEof_ThrowsEndOfStreamException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<MftRecord>(), Array.Empty<UsnJournalEntry>());

        // A 4-byte length prefix claiming a 10-byte frame, but zero body bytes before
        // the pipe closes - the distinct "EOF exactly at the frame boundary" case, as
        // opposed to EOF partway through an already-started body read.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await clientSide.WriteAsync(header);
        await clientSide.FlushAsync();
        await clientSide.DisposeAsync();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            host.ServeAsync(serverSide, CreateSectionWriter(), false, CancellationToken.None));
    }

    [TestMethod]
    public async Task StartWatch_NoWatchSourceConfigured_EmitsErrorFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        // watchDrive omitted -> null

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Broker has no watch source", frame.Message);

        await cts.CancelAsync();
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WatchSourceCompletesNaturally_EndsWithoutError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => FiniteWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))]));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

}
