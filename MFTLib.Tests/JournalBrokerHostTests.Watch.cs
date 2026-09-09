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

        using var cts = new CancellationTokenSource();
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var first = await ReadOneFrameAsync(clientSide);
        var second = await ReadOneFrameAsync(clientSide);

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:0:0"); // no cached cursor -> sentinel
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var first = await ReadOneFrameAsync(clientSide);
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
        BrokerProtocol.WriteStartWatch(startRequest, "C:7:100");
        await clientSide.WriteAsync(startRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        // Drain the single live batch, then ask the host to end the watch.
        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        await watchStarted.Task;

        var endRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatch(endRequest);
        await clientSide.WriteAsync(endRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        // The next frame the host writes must be the ack: the cancelled watch task
        // stopped yielding (FakeWatch was blocked on Infinite), so no further
        // JournalBatch can race ahead of the ack.
        var ack = await ReadOneFrameAsync(clientSide);
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
    public async Task StartWatch_DuplicateWithoutEndWatch_IsIgnored()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var watchedDrives = new List<string>();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, _, cancellationToken) =>
            {
                lock (watchedDrives)
                {
                    watchedDrives.Add(drive);
                }

                return FakeWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))], cancellationToken);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(first, "C:7:100");
        await clientSide.WriteAsync(first.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);

        // A second StartWatch without an intervening EndWatch must be ignored rather than
        // start a second generation, which is the fix for a real frame-corruption defect.
        var duplicate = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(duplicate, "D:1:0");
        await clientSide.WriteAsync(duplicate.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        // EndWatch is the ordering barrier: the host reads frames in order, so its ack
        // proves the duplicate was read and acted on. No sleep can prove that.
        var endRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatch(endRequest);
        await clientSide.WriteAsync(endRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var ack = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, ack.Kind);

        lock (watchedDrives)
        {
            CollectionAssert.AreEqual(new[] { "C" }, watchedDrives,
                "the duplicate StartWatch must not have started a watch on drive D");
        }

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);
        await serveTask;
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
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
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
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorThrowsAtStart_EmitsWarning_AndStreamsFromFreshCursor()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCount = 0;
        var freshCursor = new UsnJournalCursor(1UL, 999L);
        (UsnJournalEntry[], UsnJournalCursor)[] expectedBatch = [([SampleEntry()], new UsnJournalCursor(1UL, 1010L))];

        UsnJournalCursor QueryCursor(string drive)
        {
            queryCallCount++;
            return freshCursor;
        }

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            if (since.JournalId == 7UL)
            {
                // Cached cursor was from journal 7 which was recreated / wrapped
                throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed");
            }

            return FakeWatch(expectedBatch, cancellationToken);
        }

        var host = CreateHost(
            QueryCursor,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var firstFrame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Warning, firstFrame.Kind);
        Assert.AreEqual("C", firstFrame.Drive);
        StringAssert.Contains(firstFrame.Message, "USN journal entries have been deleted");
        StringAssert.Contains(firstFrame.Message, "replay gap was lost and a rescan is recommended");
        StringAssert.Contains(firstFrame.Message, "watching from the current journal position");

        var secondFrame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, secondFrame.Kind);
        Assert.AreEqual("C", secondFrame.Drive);
        Assert.AreEqual(new UsnJournalCursor(1UL, 1010L), secondFrame.Cursor);
        Assert.AreEqual(1, secondFrame.Entries.Length);

        Assert.AreEqual(1, queryCallCount);

        await cts.CancelAsync();
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorThrowsAndRequeryBothThrow_EmitsErrorFrameInstead()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        UsnJournalCursor QueryCursor(string drive)
        {
            throw new InvalidOperationException("volume unmounted");
        }

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed");
        }

        var host = CreateHost(
            QueryCursor,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("volume unmounted", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }
}
