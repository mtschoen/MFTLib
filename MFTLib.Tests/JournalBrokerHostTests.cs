using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostTests
{
    [TestMethod]
    public void ArmAndScan_QueriesCursorBeforeScanning()
    {
        var calls = new List<string>();
        var host = new JournalBrokerHost(
            queryCursor: drive => { calls.Add($"query:{drive}"); return new UsnJournalCursor(7UL, 0L); },
            scanDrive: drive => { calls.Add($"scan:{drive}"); return Array.Empty<ScanRecord>(); },
            readJournal: (_, since) => (Array.Empty<UsnJournalEntry>(), since));

        var (cursor, _) = host.ArmAndScan("C:");

        Assert.AreEqual(new UsnJournalCursor(7UL, 0L), cursor);
        CollectionAssert.AreEqual(ExpectedQueryThenScanCalls, calls); // query strictly first
    }

    static readonly string[] ExpectedQueryThenScanCalls = { "query:C:", "scan:C:" };

    [TestMethod]
    public void CatchUp_DelegatesToReadJournal_ReturnsAdvancedCursor()
    {
        var since = new UsnJournalCursor(7UL, 100L);
        var advanced = new UsnJournalCursor(7UL, 250L);
        var batch = new[]
        {
            UsnJournalEntry.Create(1, 5, 110, DateTime.UnixEpoch, UsnReason.Close, FileAttributes.Normal, "a"),
        };
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (drive, cursor) =>
            {
                Assert.AreEqual("C:", drive);
                Assert.AreEqual(since, cursor);
                return (batch, advanced);
            });

        var (entries, updated) = host.CatchUp("C:", since);

        Assert.AreSame(batch, entries);
        Assert.AreEqual(advanced, updated);
    }

    [TestMethod]
    public async Task ServeOnce_ArmAndScan_EmitsCursorScanReadyAndCatchUp()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(records: new[] { SampleRecord() }, catchUp: new[] { SampleEntry() });

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writeMmf = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writeMmf, oneShot: true, CancellationToken.None);
        serverSide.Dispose(); // signal EOF so the client read side completes

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[1].Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[2].Kind);
        Assert.AreEqual("C", frames[0].Drive);
        Assert.AreEqual("mftlib-scan-C", frames[1].MmfName);
        Assert.AreEqual(1, frames[2].Entries.Length);
        Assert.AreEqual(1, writeMmf.LastPayloadRecordCount);
        Assert.AreEqual("mftlib-scan-C", writeMmf.LastMmfName);
    }

    [TestMethod]
    public async Task ServeOnce_ReducedProfile_WritesDirectoriesAndGitPointersOnly()
    {
        var records = new[]
        {
            new ScanRecord(100, 5, 0, 0, 0x10, true, "repo", @"C:\repo"),
            new ScanRecord(101, 100, 0, 0, 0x20, false, ".git", @"C:\repo\.git"),
            new ScanRecord(102, 100, 0, 0, 0x20, false, "file.txt", @"C:\repo\file.txt"),
        };
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(records, Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request,
            $"C:0:0:mftlib-scan-C:{(int)BrokerScanProfile.DirectoryIndexWithGitPointers}");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writer = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writer, oneShot: true, CancellationToken.None);

        Assert.AreEqual(2, writer.LastPayloadRecordCount);
    }

    [TestMethod]
    public async Task ServeOnce_DriveFailure_EmitsErrorFrameAndContinues()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            queryCursor: _ => throw new InvalidOperationException("journal wrapped"),
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "D:0:0:mftlib-scan-D");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: true, CancellationToken.None);
        serverSide.Dispose();

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual("D", frames[0].Drive);
        Assert.AreEqual("journal wrapped", frames[0].Message);
    }

    [TestMethod]
    public async Task ServeAsync_Shutdown_ReturnsCleanly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<ScanRecord>(), Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(request);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        // oneShot false: only Shutdown should end the loop.
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, CancellationToken.None);
        serverSide.Dispose();

        Assert.AreEqual(0, ReadAllFrames(clientSide).Count);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // named memory-mapped files are a Windows facility
    public void RealMmfWriter_WritesPayload_UiCanReadItBack()
    {
        var records = new[]
        {
            new ScanRecord(5, 5, 0, 0, 0x10, true, "C:", "C:\\"),
            new ScanRecord(100, 5, 2048, 0, 0x20, false, "nöte.txt", "C:\\nöte.txt"),
        };
        var name = "mftlib-test-mmf-" + Guid.NewGuid().ToString("N");
        using var map = MemoryMappedFile.CreateNew(name, 1 << 20); // caller pre-creates a generous cap

        var written = new RealMmfWriter().Write(name, records);

        Assert.AreEqual(ScanPayload.ComputeSize(records), written);

        var buffer = new byte[written];
        using (var view = map.CreateViewStream(0, written, MemoryMappedFileAccess.Read))
            view.ReadExactly(buffer);
        var read = ScanPayload.ReadAll(buffer).ToArray();
        Assert.AreEqual(2, read.Length);
        Assert.AreEqual("C:\\nöte.txt", read[1].Path);
    }

    [TestMethod]
    public async Task StartWatch_StreamsBatches_UntilCancelled()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var batches = new[]
        {
            (new[] { SampleEntry() }, new UsnJournalCursor(7UL, 110L)),
            (new[] { SampleEntry() }, new UsnJournalCursor(7UL, 120L)),
        };
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, _, cancellationToken) => FakeWatch(batches, cancellationToken));

        using var cts = new CancellationTokenSource();
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var first = await ReadOneFrameAsync(clientSide);
        var second = await ReadOneFrameAsync(clientSide);

        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, second.Kind);
        Assert.AreEqual("C", first.Drive);
        Assert.AreEqual(new UsnJournalCursor(7UL, 110L), first.Cursor);
        Assert.AreEqual(new UsnJournalCursor(7UL, 120L), second.Cursor);

        cts.Cancel();
        await serveTask; // returns cleanly once cancelled
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursor_QueriesCurrentCursorBeforeWatching()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        UsnJournalCursor watchedFrom = default;
        var host = new JournalBrokerHost(
            queryCursor: _ => new UsnJournalCursor(9UL, 500L),
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, since, cancellationToken) =>
            {
                watchedFrom = since;
                return FakeWatch(new[] { (new[] { SampleEntry() }, new UsnJournalCursor(9UL, 510L)) }, cancellationToken);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:0:0"); // no cached cursor -> sentinel
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var first = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(new UsnJournalCursor(9UL, 500L), watchedFrom);

        cts.Cancel();
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
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, _, cancellationToken) =>
            {
                watchStarted.TrySetResult();
                return FakeWatch(new[] { (new[] { SampleEntry() }, new UsnJournalCursor(7UL, 110L)) }, cancellationToken);
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(startRequest, "C:7:100");
        await clientSide.WriteAsync(startRequest.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

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
        var host = MakeFakeHost(Array.Empty<ScanRecord>(), Array.Empty<UsnJournalEntry>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);
        var finished = await Task.WhenAny(serveTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.AreSame(serveTask, finished);
        await serveTask;
    }

    [TestMethod]
    public async Task ServeAsync_ClientClosesAfterOneRequest_ReturnsCleanlyOnEof()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<ScanRecord>(), Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();
        clientSide.Dispose(); // close before Shutdown - the host's second read hits clean EOF

        // oneShot: false so ServeAsync loops back and must observe the EOF itself.
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, CancellationToken.None);
    }

    [TestMethod]
    public async Task ServeAsync_TruncatedFrameBody_ThrowsEndOfStreamException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<ScanRecord>(), Array.Empty<UsnJournalEntry>());

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10); // claims a 10-byte frame
        await clientSide.WriteAsync(header);
        await clientSide.WriteAsync(new byte[] { 1, 2, 3 }); // delivers only 3
        await clientSide.FlushAsync();
        clientSide.Dispose(); // EOF partway through the frame body

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task ServeAsync_HeaderOnlyThenEof_ThrowsEndOfStreamException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(Array.Empty<ScanRecord>(), Array.Empty<UsnJournalEntry>());

        // A 4-byte length prefix claiming a 10-byte frame, but zero body bytes before
        // the pipe closes - the distinct "EOF exactly at the frame boundary" case, as
        // opposed to EOF partway through an already-started body read.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await clientSide.WriteAsync(header);
        await clientSide.FlushAsync();
        clientSide.Dispose();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task StartWatch_DuplicateWithoutEndWatch_IsIgnored()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, _, cancellationToken) =>
                FakeWatch(new[] { (new[] { SampleEntry() }, new UsnJournalCursor(7UL, 110L)) }, cancellationToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(first, "C:7:100");
        await clientSide.WriteAsync(first.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);

        // A second StartWatch without an intervening EndWatch must be ignored (not
        // restart the running generation). Give the host a moment to process and
        // discard it before shutting the session down.
        var duplicate = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(duplicate, "D:1:0");
        await clientSide.WriteAsync(duplicate.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);
        await Task.Delay(50, cts.Token);

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
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        // watchDrive omitted -> null

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Broker has no watch source", frame.Message);

        cts.Cancel();
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WatchSourceCompletesNaturally_EndsWithoutError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, _, _) => FiniteWatch(new[] { (new[] { SampleEntry() }, new UsnJournalCursor(7UL, 110L)) }));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory);
        await clientSide.FlushAsync();
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WatchSourceThrows_EmitsErrorFrame_SessionContinues()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            queryCursor: _ => default,
            scanDrive: _ => Array.Empty<ScanRecord>(),
            readJournal: (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            watchDrive: (_, _, _) => ThrowingWatch());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("journal wrapped mid-stream", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory);
        await clientSide.FlushAsync();
        await serveTask;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FakeWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
            yield return batch;
        // A live watch stays open until cancelled; mimic that so the serve loop
        // does not end before the test cancels.
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Yields its batches and then completes normally (no infinite delay), so a
    // consumer's await-foreach exits without cancellation or an exception.
    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FiniteWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches)
    {
        foreach (var batch in batches)
            yield return batch;
        await Task.CompletedTask;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ThrowingWatch()
    {
        await Task.Yield();
        throw new InvalidOperationException("journal wrapped mid-stream");
#pragma warning disable CS0162
        // Required so the compiler emits the iterator state machine for
        // IAsyncEnumerable; the throw above is unconditional and intentional (a test
        // double for a watch source that fails immediately).
        // aislop-ignore-next-line HeuristicUnreachableCode
        yield break;
#pragma warning restore CS0162
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    static ScanRecord SampleRecord() => new(100, 5, 2048, 0, 0x20, false, "a.txt", "C:\\a.txt");

    static UsnJournalEntry SampleEntry() => UsnJournalEntry.Create(
        100, 5, 110, DateTime.UnixEpoch, UsnReason.FileCreate | UsnReason.Close, FileAttributes.Normal, "a.txt");

    static JournalBrokerHost MakeFakeHost(ScanRecord[] records, UsnJournalEntry[] catchUp) => new(
        queryCursor: _ => new UsnJournalCursor(7UL, 0L),
        scanDrive: _ => records,
        readJournal: (_, cursor) => (catchUp, new UsnJournalCursor(cursor.JournalId, cursor.NextUsn + catchUp.Length)));

    static List<BrokerFrame> ReadAllFrames(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var frames = new List<BrokerFrame>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var frame = BrokerProtocol.ReadFrame(bytes.AsSpan(offset), out var consumed);
            frames.Add(frame);
            offset += consumed;
        }
        return frames;
    }
}
