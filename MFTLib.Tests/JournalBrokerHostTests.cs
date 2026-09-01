using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostTests
{
    static readonly string[] ExpectedQueryThenScanCalls = ["query:C:", "scan:C:"];

    static readonly string[] KeepFileNamesGit = [".git"];
    static readonly string[] KeepFileNamesGitUppercase = [".GIT"];
    static readonly string[] KeepFileNamesNonMatching = ["other.txt"];

    static readonly ScanRecord[] DirectoryIndexSampleRecords =
    [
        new(100, 5, 0, 0, 0x10, true, "repo", @"C:\repo"),
        new(101, 100, 0, 0, 0x20, false, ".git", @"C:\repo\.git"),
        new(102, 100, 0, 0, 0x20, false, "file.txt", @"C:\repo\file.txt")
    ];

    [TestMethod]
    public void ArmAndScan_QueriesCursorBeforeScanning()
    {
        var calls = new List<string>();
        var host = new JournalBrokerHost(
            drive =>
            {
                calls.Add($"query:{drive}");
                return new UsnJournalCursor(7UL, 0L);
            },
            drive =>
            {
                calls.Add($"scan:{drive}");
                return Array.Empty<ScanRecord>();
            },
            (_, since) => (Array.Empty<UsnJournalEntry>(), since));

        var (cursor, _) = host.ArmAndScan("C:");

        Assert.AreEqual(new UsnJournalCursor(7UL, 0L), cursor);
        CollectionAssert.AreEqual(ExpectedQueryThenScanCalls, calls); // query strictly first
    }

    [TestMethod]
    public void CatchUp_DelegatesToReadJournal_ReturnsAdvancedCursor()
    {
        var since = new UsnJournalCursor(7UL, 100L);
        var advanced = new UsnJournalCursor(7UL, 250L);
        var batch = new[]
        {
            JournalEntryFactory.Create(1, 110, "a")
        };
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (drive, cursor) =>
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
        var host = MakeFakeHost([SampleRecord()], [SampleEntry()]);

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writeMmf = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
        await serverSide.DisposeAsync(); // signal EOF so the client read side completes

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        var scanReady = frames.Single(f => f.Kind == BrokerFrameKind.ScanReady);
        var journalBatch = frames.Single(f => f.Kind == BrokerFrameKind.JournalBatch);
        Assert.AreEqual("C", frames[0].Drive);
        Assert.AreEqual("mftlib-scan-C", scanReady.MmfName);
        Assert.AreEqual(1, journalBatch.Entries.Length);
        Assert.AreEqual(1, writeMmf.LastPayloadRecordCount);
        Assert.AreEqual("mftlib-scan-C", writeMmf.LastMmfName);
    }

    [TestMethod]
    public async Task ServeOnce_StreamingDriveScanSource_StreamsBatchesToStreamingMmfWriter()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _) =>
            [
                [new ScanRecord(1, 0, 100, 0, 0x20, false, "batch1.txt", @"C:\batch1.txt")],
                [new ScanRecord(2, 0, 200, 0, 0x20, false, "batch2.txt", @"C:\batch2.txt")]
            ],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-streaming-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writeMmf = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        var scanReady = frames.Single(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.AreEqual(2, scanReady.RecordCount);
        Assert.AreEqual(2, writeMmf.WrittenBatches.Count);
        Assert.AreEqual(2, writeMmf.LastPayloadRecordCount);
    }

    [TestMethod]
    public async Task ServeOnce_LegacyNonStreamingMmfWriter_AdaptsBatchesToArray()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _) =>
            [
                [new ScanRecord(1, 0, 100, 0, 0x20, false, "b1.txt", @"C:\b1.txt")],
                [new ScanRecord(2, 0, 200, 0, 0x20, false, "b2.txt", @"C:\b2.txt")]
            ],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-legacy-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var legacyWriter = new LegacyMmfWriterDouble();
        await host.ServeAsync(serverSide, legacyWriter, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        var scanReady = frames.Single(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.AreEqual(2, scanReady.RecordCount);
        Assert.AreEqual(2, legacyWriter.LastRecords.Length);
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_KeepsTheNamedFile()
    {
        var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, KeepFileNamesGit);

        Assert.AreEqual(2, writer.LastPayloadRecordCount); // repo (directory) + .git (named match)
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_IsCaseInsensitive()
    {
        var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, KeepFileNamesGitUppercase);

        Assert.AreEqual(2, writer.LastPayloadRecordCount); // repo (directory) + .git (matched despite case)
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_NonMatchingFiles_AreDropped()
    {
        var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, KeepFileNamesNonMatching);

        Assert.AreEqual(1, writer.LastPayloadRecordCount); // repo (directory) only
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_NullKeepFileNames_YieldsDirectoriesOnly()
    {
        var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, null);

        Assert.AreEqual(1, writer.LastPayloadRecordCount); // repo (directory) only
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_EmptyKeepFileNames_YieldsDirectoriesOnly()
    {
        var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, Array.Empty<string>());

        Assert.AreEqual(1, writer.LastPayloadRecordCount); // repo (directory) only
    }

    static async Task<RecordingMmfWriter> ServeDirectoryIndexAsync(
        ScanRecord[] records, IReadOnlyCollection<string>? keepFileNames)
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost(records, Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request,
            $"C:0:0:mftlib-scan-C:{(int)BrokerScanProfile.DirectoryIndex}", keepFileNames);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writer = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writer, true, CancellationToken.None);
        return writer;
    }

    [TestMethod]
    public async Task ServeOnce_UnknownProfileToken_ThrowsInvalidDataException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = MakeFakeHost([SampleRecord()], Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C:99");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            host.ServeAsync(serverSide, new RecordingMmfWriter(), true, CancellationToken.None));
        StringAssert.Contains(exception.Message, "99");
    }

    [TestMethod]
    public void ApplyScanProfile_UnknownProfile_ThrowsInvalidDataException()
    {
        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            JournalBrokerHost.ApplyScanProfile(Array.Empty<ScanRecord>(), (BrokerScanProfile)99,
                Array.Empty<string>()));
        StringAssert.Contains(exception.Message, "99");
    }

    [TestMethod]
    public async Task ServeOnce_DriveFailure_EmitsErrorFrameAndContinues()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => throw new InvalidOperationException("journal wrapped"),
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "D:0:0:mftlib-scan-D");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, new RecordingMmfWriter(), true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual("D", frames[0].Drive);
        Assert.AreEqual("journal wrapped", frames[0].Message);
    }

    [TestMethod]
    public async Task ServeOnce_MmfCapacityExceeded_EmitsErrorFrameNamingDriveAndOptions()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(1UL, 100L),
            (_, _) =>
            [
                [new ScanRecord(1, 0, 100, 0, 0x20, false, "overflow.txt", @"C:\overflow.txt")]
            ],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var failingWriter = new FailingMmfWriterDouble(
            new InvalidOperationException(
                "Scan payload exceeded the shared-memory map capacity (100 bytes) after 1 records; increase BrokerScanOptions.MmfCapacityBytes."));

        await host.ServeAsync(serverSide, failingWriter, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        var error = frames.Single(f => f.Kind == BrokerFrameKind.Error);
        Assert.AreEqual("C", error.Drive);
        StringAssert.Contains(error.Message, "for drive C");
        StringAssert.Contains(error.Message, "exceeded the shared-memory map capacity");
        StringAssert.Contains(error.Message, "BrokerScanOptions.MmfCapacityBytes");
    }

    [TestMethod]
    public async Task ServeOnce_CatchUpThrows_EmitsWarningAndZeroEntryJournalBatch_OtherDriveUnaffected()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCounts = new Dictionary<string, int>();
        var armedCursorForC = new UsnJournalCursor(1UL, 100L);
        var freshCursorForC = new UsnJournalCursor(1UL, 999L);
        var cursorForD = new UsnJournalCursor(2UL, 200L);

        UsnJournalCursor QueryCursor(string drive)
        {
            var count = queryCallCounts.TryGetValue(drive, out var existing) ? existing + 1 : 1;
            queryCallCounts[drive] = count;
            return drive switch
            {
                "C" => count == 1 ? armedCursorForC : freshCursorForC, // arm, then re-query after catch-up fails
                "D" => cursorForD,
                _ => throw new InvalidOperationException($"unexpected drive {drive}")
            };
        }

        (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since)
        {
            if (drive == "C")
            {
                throw new InvalidOperationException("journal wrapped");
            }

            return (Array.Empty<UsnJournalEntry>(), since);
        }

        var host = new JournalBrokerHost(QueryCursor, _ => Array.Empty<ScanRecord>(), ReadJournal);

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C,D:0:0:mftlib-scan-D");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, new RecordingMmfWriter(), true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);

        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error), "No Error frame for either drive");
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.AreEqual("C", frames[0].Drive);

        var warning = frames.Single(f => f.Kind == BrokerFrameKind.Warning);
        Assert.AreEqual("C", warning.Drive);
        StringAssert.Contains(warning.Message, "journal wrapped");
        StringAssert.Contains(warning.Message, "watching from the current journal position");

        var journalBatches = frames.Where(f => f.Kind == BrokerFrameKind.JournalBatch).ToList();
        Assert.AreEqual(2, journalBatches.Count);

        var batchC = journalBatches.Single(f => f.Drive == "C");
        Assert.AreEqual(freshCursorForC, batchC.Cursor);
        Assert.AreEqual(0, batchC.Entries.Length);

        var batchD = journalBatches.Single(f => f.Drive == "D");
        Assert.AreEqual(cursorForD, batchD.Cursor);
        Assert.AreEqual(0, batchD.Entries.Length);

        // Ordering for C: ScanReady precedes the Warning, which precedes the
        // JournalBatch it substitutes for a normal successful catch-up.
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady); // C is processed first
        var warningIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.Warning && f.Drive == "C");
        var batchCIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.JournalBatch && f.Drive == "C");
        Assert.IsTrue(scanReadyIndex < warningIndex, "Warning must come after ScanReady");
        Assert.IsTrue(warningIndex < batchCIndex, "Warning must come before its JournalBatch");
    }

    [TestMethod]
    public async Task ServeOnce_CatchUpAndRequeryBothThrow_EmitsErrorFrameInstead()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCount = 0;

        UsnJournalCursor QueryCursor(string _)
        {
            queryCallCount++;
            if (queryCallCount == 1)
            {
                return new UsnJournalCursor(1UL, 100L); // arm succeeds
            }

            throw new InvalidOperationException("volume closed"); // re-query also fails
        }

        var host = new JournalBrokerHost(
            QueryCursor,
            _ => Array.Empty<ScanRecord>(),
            (_, _) => throw new InvalidOperationException("journal wrapped"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, new RecordingMmfWriter(), true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);

        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Warning),
            "No Warning frame when the re-query also fails");
        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.JournalBatch),
            "No JournalBatch when the re-query also fails");

        var error = frames.Single(f => f.Kind == BrokerFrameKind.Error);
        Assert.AreEqual("C", error.Drive);
        Assert.AreEqual("volume closed", error.Message);
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
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), false, CancellationToken.None);
        await serverSide.DisposeAsync();

        Assert.AreEqual(0, ReadAllFrames(clientSide).Count);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // named memory-mapped files are a Windows facility
    public void RealMmfWriter_WritesPayload_UiCanReadItBack()
    {
        var records = new[]
        {
            new ScanRecord(5, 5, 0, 0, 0x10, true, "C:", "C:\\"),
            new ScanRecord(100, 5, 2048, 0, 0x20, false, "nöte.txt", "C:\\nöte.txt")
        };
        var name = "mftlib-test-mmf-" + Guid.NewGuid().ToString("N");
        using var map = MemoryMappedFile.CreateNew(name, 1 << 20); // caller pre-creates a generous cap

        var written = new RealMmfWriter().Write(name, records);

        Assert.AreEqual(ScanPayload.ComputeSize(records), written);

        var buffer = new byte[written];
        using (var view = map.CreateViewStream(0, written, MemoryMappedFileAccess.Read))
        {
            view.ReadExactly(buffer);
        }

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
            ([SampleEntry()], new UsnJournalCursor(7UL, 120L))
        };
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(batches, cancellationToken));

        using var cts = new CancellationTokenSource();
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(9UL, 500L),
            _ => Array.Empty<ScanRecord>(),
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

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
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

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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
        await cts.CancelAsync();

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);
        var finished = await Task.WhenAny(serveTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

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
        await clientSide.DisposeAsync(); // close before Shutdown - the host's second read hits clean EOF

        // oneShot: false so ServeAsync loops back and must observe the EOF itself.
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), false, CancellationToken.None);
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
        await clientSide.DisposeAsync(); // EOF partway through the frame body

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            host.ServeAsync(serverSide, new RecordingMmfWriter(), false, CancellationToken.None));
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
        await clientSide.DisposeAsync();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() =>
            host.ServeAsync(serverSide, new RecordingMmfWriter(), false, CancellationToken.None));
    }

    [TestMethod]
    public async Task StartWatch_DuplicateWithoutEndWatch_IsIgnored()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) =>
                FakeWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))], cancellationToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(first, "C:7:100");
        await clientSide.WriteAsync(first.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        // watchDrive omitted -> null

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => FiniteWatch([([SampleEntry()], new UsnJournalCursor(7UL, 110L))]));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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

        var host = new JournalBrokerHost(
            QueryCursor,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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

        var host = new JournalBrokerHost(
            QueryCursor,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

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

    [TestMethod]
    public async Task StartWatch_CachedCursorThrowsAtStart_OneDriveDegrades_OtherDriveStreamsNormally()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursorC = new UsnJournalCursor(1UL, 999L);
        (UsnJournalEntry[], UsnJournalCursor)[] batchC = [([SampleEntry()], new UsnJournalCursor(1UL, 1010L))];
        (UsnJournalEntry[], UsnJournalCursor)[] batchD = [([SampleEntry()], new UsnJournalCursor(2UL, 210L))];

        UsnJournalCursor QueryCursor(string drive)
        {
            return drive == "C" ? freshCursorC : throw new InvalidOperationException("unexpected query");
        }

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            if (drive == "C" && since.JournalId == 7UL)
            {
                throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed");
            }

            return FakeWatch(drive == "C" ? batchC : batchD, cancellationToken);
        }

        var host = new JournalBrokerHost(
            QueryCursor,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100,D:2:200");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var frames = new List<BrokerFrame>();
        for (var i = 0; i < 3; i++)
        {
            frames.Add(await ReadOneFrameAsync(clientSide));
        }

        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error), "No Error frames");
        var warning = frames.Single(f => f.Kind == BrokerFrameKind.Warning);
        Assert.AreEqual("C", warning.Drive);
        StringAssert.Contains(warning.Message, "USN journal entries have been deleted");

        var batches = frames.Where(f => f.Kind == BrokerFrameKind.JournalBatch).ToList();
        Assert.AreEqual(2, batches.Count);
        Assert.IsTrue(batches.Any(b => b.Drive == "C" && b.Cursor == new UsnJournalCursor(1UL, 1010L)));
        Assert.IsTrue(batches.Any(b => b.Drive == "D" && b.Cursor == new UsnJournalCursor(2UL, 210L)));

        await cts.CancelAsync();
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_MidStreamThrows_EmitsErrorFrame_SessionContinues()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => ThrowingWatchMidStream());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("C", batch.Drive);

        var error = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("C", error.Drive);
        Assert.AreEqual("journal wrapped mid-stream", error.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_GenericStartupFailure_WithSuccessfulQueryCursor_EmitsErrorFrame_DoesNotEmitWarning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursor = new UsnJournalCursor(7UL, 200L);

        var host = new JournalBrokerHost(
            _ => freshCursor,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException("Access is denied"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Access is denied", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorSameAsCurrentCursor_Fails_EmitsErrorFrame_DoesNotEmitWarning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var cursor = new UsnJournalCursor(7UL, 100L);

        var host = new JournalBrokerHost(
            _ => cursor,
            _ => Array.Empty<ScanRecord>(),
            (_, c) => (Array.Empty<UsnJournalEntry>(), c),
            (_, _, _) => throw new InvalidOperationException("Cannot open volume"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Cannot open volume", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorFails_RequeryThrows_EmitsErrorFrame_DoesNotEmitWarning()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var host = new JournalBrokerHost(
            _ => throw new InvalidOperationException("Volume disappeared"),
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException("USN journal entries have been deleted; full rescan needed"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Volume disappeared", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_CachedCursorJournalIdMismatch_DegradesToWarningAndStreams()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var freshCursor = new UsnJournalCursor(8UL, 50L);
        (UsnJournalEntry[], UsnJournalCursor)[] batches = [([SampleEntry()], new UsnJournalCursor(8UL, 60L))];

        IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> WatchDrive(
            string drive, UsnJournalCursor since, CancellationToken cancellationToken)
        {
            if (since.JournalId == 7UL)
            {
                throw new InvalidOperationException("Journal ID mismatch");
            }

            return FakeWatch(batches, cancellationToken);
        }

        var host = new JournalBrokerHost(
            _ => freshCursor,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            WatchDrive);

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var warning = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Warning, warning.Kind);
        Assert.AreEqual("C", warning.Drive);
        StringAssert.Contains(warning.Message, "Journal ID mismatch");

        var batch = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("C", batch.Drive);
        Assert.AreEqual(new UsnJournalCursor(8UL, 60L), batch.Cursor);

        await cts.CancelAsync();
        await serveTask;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> FakeWatch(
        (UsnJournalEntry[], UsnJournalCursor)[] batches,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
        {
            yield return batch;
        }

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
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[], UsnJournalCursor)> ThrowingWatchMidStream()
    {
        yield return ([SampleEntry()], new UsnJournalCursor(7UL, 110L));
        await Task.Yield();
        throw new InvalidOperationException("journal wrapped mid-stream");
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

    static ScanRecord SampleRecord()
    {
        return new ScanRecord(100, 5, 2048, 0, 0x20, false, "a.txt", "C:\\a.txt");
    }

    static UsnJournalEntry SampleEntry()
    {
        return JournalEntryFactory.Create(
            100, 110, "a.txt", UsnReason.FileCreate | UsnReason.Close);
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_EmitsFinalFrameImmediatelyBeforeScanReady()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _) =>
            [
                [new ScanRecord(1, 0, 100, 0, 0x20, false, "r1.txt", @"C:\r1.txt")],
                [new ScanRecord(2, 0, 200, 0, 0x20, false, "r2.txt", @"C:\r2.txt")]
            ],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-progress-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writeMmf = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsTrue(frames.Count >= 3, "Expected at least Cursor, ScanProgress, and ScanReady frames");
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);

        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present");
        var finalProgressIndex = scanReadyIndex - 1;
        Assert.AreEqual(BrokerFrameKind.ScanProgress, frames[finalProgressIndex].Kind,
            "Final progress frame must immediately precede ScanReady");
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[scanReadyIndex + 1].Kind);

        var progress = frames[finalProgressIndex].Progress;
        Assert.IsNotNull(progress);
        Assert.AreEqual("C", progress.Value.DriveLetter);
        Assert.AreEqual(2, progress.Value.RecordsProcessed);
        Assert.AreEqual(2, progress.Value.TotalRecords);
        Assert.IsTrue(progress.Value.BytesProcessed > 0);
        Assert.AreEqual(progress.Value.BytesProcessed, progress.Value.TotalBytes);
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_ThrottlesNonFinalFrames()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.FromMinutes(10);

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            var host = new JournalBrokerHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, progress, _) =>
                {
                    var batches = new List<IReadOnlyList<ScanRecord>>();
                    for (var i = 0; i < 20; i++)
                    {
                        progress?.Report(new MmfWriteProgress(i, i * 100, 20, 2000));
                        batches.Add([new ScanRecord((ulong)i, 0, 100, 0, 0x20, false, $"r{i}.txt", $@"C:\r{i}.txt")]);
                    }

                    return batches;
                },
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-throttle-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            var writeMmf = new RecordingMmfWriter();
            await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();
            // With a 10-minute throttle interval, the rapid burst of 20 reports emits at most 1 initial non-final frame
            // plus 1 final synthetic frame before ScanReady (at most 2 total progress frames).
            Assert.IsTrue(progressFrames.Count >= 1, "At least one progress frame must be emitted");
            Assert.IsTrue(progressFrames.Count <= 2,
                $"Expected at most 2 progress frames due to throttling, but got {progressFrames.Count}");
            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
            Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex,
                "Final progress frame must immediately precede ScanReady");
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_CancelledScan_EndsCleanlyWithoutErrorFrame()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, ct) => throw new OperationCanceledException(ct),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-cancel-C");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var writeMmf = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writeMmf, true, cts.Token);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error),
            "Cancellation must not emit an Error frame");
    }


    [TestMethod]
    public async Task ServeOnce_ScanProgress_MonotonicAcrossParseAndWritePhases()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            // Zero, not a small interval: a non-zero throttle makes emission depend on
            // real elapsed time between reports, which is what made this test flake on
            // CI. With Zero the pump emits every value it manages to read.
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            var host = new JournalBrokerHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, progress, _) =>
                {
                    for (var i = 1; i <= 5; i++)
                    {
                        progress?.Report(new MmfWriteProgress(
                            i * 1000,
                            0,
                            5000,
                            null));
                    }

                    return
                    [
                        [new ScanRecord(1, 0, 100, 0, 0x20, false, "r1.txt", @"C:\r1.txt")],
                        [new ScanRecord(2, 0, 200, 0, 0x20, false, "r2.txt", @"C:\r2.txt")],
                        [new ScanRecord(3, 0, 300, 0, 0x20, false, "r3.txt", @"C:\r3.txt")]
                    ];
                },
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-monotonic-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            var writeMmf = new RecordingMmfWriter();
            await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            // Only the final frame is guaranteed. Intermediate frames flow through a
            // bounded(1)/DropOldest channel, so the pump silently loses any value it has
            // not read before the next write - how many survive depends on thread
            // scheduling, not on behaviour under test. Asserting a count above one is
            // what made this flaky. The invariants below hold for whatever does arrive.
            Assert.IsTrue(progressFrames.Count >= 1, "The final progress frame is always emitted");

            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            var lastProgressIndex = frames.FindLastIndex(f => f.Kind == BrokerFrameKind.ScanProgress);
            Assert.AreEqual(scanReadyIndex - 1, lastProgressIndex,
                "Final progress frame must immediately precede ScanReady");

            long previousRecords = 0;
            var previousElapsed = TimeSpan.Zero;
            foreach (var frame in progressFrames)
            {
                Assert.IsNotNull(frame.Progress);
                var p = frame.Progress.Value;
                Assert.AreEqual("C", p.DriveLetter);
                Assert.IsTrue(p.RecordsProcessed >= previousRecords,
                    $"RecordsProcessed must be non-decreasing: got {p.RecordsProcessed}, previous was {previousRecords}");
                // aislop-ignore-next-line ai-slop/test-wall-clock-assertion -- false positive: MftScanProgress.Elapsed is a constructor-injected TimeSpan carried in the frame, not a clock read (schoen/aislop#51)
                Assert.IsTrue(p.Elapsed >= previousElapsed,
                    $"Elapsed must be non-decreasing: got {p.Elapsed}, previous was {previousElapsed}");
                Assert.AreEqual(5000L, p.TotalRecords, "TotalRecords must stay 5000 throughout");
                previousRecords = p.RecordsProcessed;
                previousElapsed = p.Elapsed;
            }

            var finalProgress = progressFrames.Last().Progress!.Value;
            Assert.AreEqual(5000L, finalProgress.RecordsProcessed);
            Assert.AreEqual(5000L, finalProgress.TotalRecords);
            Assert.IsTrue(finalProgress.BytesProcessed > 0);
            Assert.AreEqual(finalProgress.BytesProcessed, finalProgress.TotalBytes);
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_WritePhaseByteProgressFlowsThroughBeforeCompletion()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            // The fake scan source reports no progress of its own (mirroring the
            // production ScanDriveBatches parse-phase adapter, which always reports
            // BytesProcessed = 0), so the only way a ScanProgress frame with
            // BytesProcessed > 0 can reach the wire before the guaranteed completion
            // frame is if ExecuteDriveScanAsync wires the same progress reporter into
            // the streaming writer's Write call, letting the write phase's own
            // per-batch byte progress (RecordingMmfWriter below) flow through.
            var host = new JournalBrokerHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, _, _) =>
                [
                    [new ScanRecord(1, 0, 100, 0, 0x20, false, "r1.txt", @"C:\r1.txt")],
                    [new ScanRecord(2, 0, 200, 0, 0x20, false, "r2.txt", @"C:\r2.txt")]
                ],
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-writebytes-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            var writeMmf = new RecordingMmfWriter();
            await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
            Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present");
            var finalProgressIndex = scanReadyIndex - 1;

            var beforeCompletion = frames.Take(finalProgressIndex)
                .Where(f => f.Kind == BrokerFrameKind.ScanProgress)
                .ToList();
            Assert.IsTrue(beforeCompletion.Count > 0,
                "Expected a write-phase ScanProgress frame on the wire before the guaranteed completion frame");
            Assert.IsTrue(beforeCompletion.All(f => f.Progress!.Value.BytesProcessed > 0),
                "Write-phase progress frames must report nonzero bytes processed");
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_PumpCancelledBeforeAnyProgressReported_SwallowsCancellationInPump()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        using var cts = new CancellationTokenSource();
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, ct) =>
            {
                scanStarted.TrySetResult();
                // Block without ever reporting progress, so the progress channel stays
                // empty and RunProgressPumpAsync stays parked in its first
                // reader.WaitToReadAsync call when the caller cancels - the condition
                // needed to hit the pump's own catch (OperationCanceledException)
                // rather than observing the channel complete normally afterward.
                ct.WaitHandle.WaitOne();
                throw new OperationCanceledException(ct);
            },
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-pump-cancel-C");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var writeMmf = new RecordingMmfWriter();
        var serveTask = host.ServeAsync(serverSide, writeMmf, true, cts.Token);

        await scanStarted.Task;
        await cts.CancelAsync();

        var finished = await Task.WhenAny(serveTask, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.AreSame(serveTask, finished, "ServeAsync must return promptly after cancellation, not hang");
        await serveTask;
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsFalse(frames.Any(f => f.Kind == BrokerFrameKind.Error),
            "Cancellation must not emit an Error frame, and the progress pump's own cancellation must be swallowed internally");
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_MaxRecordsProcessedExceedsReportedTotals_ClampsFinalCounts()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, progress, _) =>
            {
                // Report more records processed than the (deliberately understated)
                // TotalRecords estimate, so EmitScanCompletionFramesAsync's clamp has to
                // bump both finalRecords and finalTotalRecords up to maxRecordsProcessed
                // instead of trusting the last reported total.
                progress?.Report(new MmfWriteProgress(10, 500, 5, 1000));
                return
                [
                    [new ScanRecord(1, 0, 100, 0, 0x20, false, "r1.txt", @"C:\r1.txt")]
                ];
            },
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-clamp-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var writeMmf = new RecordingMmfWriter();
        await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(scanReadyIndex > 0, "ScanReady frame must be present");
        var finalProgress = frames[scanReadyIndex - 1].Progress;
        Assert.IsNotNull(finalProgress);
        Assert.AreEqual(10L, finalProgress.Value.RecordsProcessed,
            "finalRecords must be clamped up to maxRecordsProcessed when the reported total undercounts it");
        Assert.AreEqual(10L, finalProgress.Value.TotalRecords,
            "finalTotalRecords must be clamped up to the clamped finalRecords, not left at the stale reported total");
    }

    [TestMethod]
    public void ArmAndScanBatches_ProgressStreamingSource_PassesProgressCallback()
    {
        MmfWriteProgress? reportedProgress = null;
        var progress = new DirectProgress(p => reportedProgress = p);

        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, p, _) =>
            {
                p?.Report(new MmfWriteProgress(500, 10000, 1500, 30000));
                return [[SampleRecord()]];
            },
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var (_, batches) = host.ArmAndScanBatches("C", progress);
        var enumerated = batches.ToList();

        Assert.AreEqual(1, enumerated.Count);
        Assert.IsNotNull(reportedProgress);
        Assert.AreEqual(500L, reportedProgress.Value.RecordsProcessed);
        Assert.AreEqual(1500L, reportedProgress.Value.TotalRecords);
    }

    [TestMethod]
    public async Task ServeOnce_ScanProgress_WritePhase_PassesProgressToStreamingWriterAndReportsBytes()
    {
        var originalThrottle = JournalBrokerHost._progressThrottleInterval;
        try
        {
            JournalBrokerHost._progressThrottleInterval = TimeSpan.Zero;

            var (clientSide, serverSide) = DuplexStream.CreatePair();
            var host = new JournalBrokerHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, progress, _) =>
                {
                    progress?.Report(new MmfWriteProgress(100, 0, 1000, null));
                    return
                    [
                        [new ScanRecord(1, 0, 100, 0, 0x20, false, "r1.txt", @"C:\r1.txt")],
                        [new ScanRecord(2, 0, 200, 0, 0x20, false, "r2.txt", @"C:\r2.txt")]
                    ];
                },
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-write-prog-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            var writeMmf = new RecordingMmfWriter();
            await host.ServeAsync(serverSide, writeMmf, true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var progressFrames = frames.Where(f => f.Kind == BrokerFrameKind.ScanProgress).ToList();

            Assert.IsTrue(progressFrames.Count >= 2, "Expected intermediate parse and write progress frames");
            Assert.IsTrue(progressFrames.Any(f => f.Progress?.BytesProcessed > 0),
                "At least one progress frame must report BytesProcessed > 0 from the write phase");

            var finalProgress = progressFrames.Last().Progress!.Value;
            Assert.AreEqual(1000L, finalProgress.TotalRecords);
            Assert.IsTrue(finalProgress.BytesProcessed > 0);
            Assert.AreEqual(finalProgress.BytesProcessed, finalProgress.TotalBytes);
        }
        finally
        {
            JournalBrokerHost._progressThrottleInterval = originalThrottle;
        }
    }

    static JournalBrokerHost MakeFakeHost(ScanRecord[] records, UsnJournalEntry[] catchUp)
    {
        return new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            _ => records,
            (_, cursor) => (catchUp, new UsnJournalCursor(cursor.JournalId, cursor.NextUsn + catchUp.Length)));
    }

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

    sealed class LegacyMmfWriterDouble : IMmfWriter
    {
        public ScanRecord[] LastRecords { get; private set; } = Array.Empty<ScanRecord>();

        public long Write(string mmfName, ScanRecord[] records)
        {
            LastRecords = records;
            return 1234;
        }
    }

    sealed class FailingMmfWriterDouble(Exception exception) : IStreamingMmfWriter
    {
        public long Write(string mmfName, ScanRecord[] records) => throw exception;

        public MmfWriteResult Write(
            string mmfName, IEnumerable<IReadOnlyList<ScanRecord>> batches, CancellationToken cancellationToken) =>
            throw exception;

        public MmfWriteResult Write(
            string mmfName, IEnumerable<IReadOnlyList<ScanRecord>> batches,
            IProgress<MmfWriteProgress>? progress, CancellationToken cancellationToken) =>
            throw exception;
    }

    sealed class DirectProgress(Action<MmfWriteProgress> handler) : IProgress<MmfWriteProgress>
    {
        public void Report(MmfWriteProgress value)
        {
            handler(value);
        }
    }
}
