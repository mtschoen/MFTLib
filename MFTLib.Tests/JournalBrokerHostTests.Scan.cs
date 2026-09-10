using MFTLib.Index;
using System.Buffers;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public void CatchUp_DelegatesToReadJournal_ReturnsAdvancedCursor()
    {
        var since = new UsnJournalCursor(7UL, 100L);
        var advanced = new UsnJournalCursor(7UL, 250L);
        var batch = new[]
        {
            JournalEntryFactory.Create(1, 110, "a")
        };
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
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

        using var blockWriter = CreateSectionWriter();
        await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
        await serverSide.DisposeAsync(); // signal EOF so the client read side completes

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        var scanReady = frames.Single(f => f.Kind == BrokerFrameKind.ScanReady);
        var journalBatch = frames.Single(f => f.Kind == BrokerFrameKind.JournalBatch);
        Assert.AreEqual("C", frames[0].Drive);
        Assert.AreEqual("mftlib-scan-C", scanReady.MmfName);
        Assert.AreEqual(1, journalBatch.Entries.Length);
        Assert.AreEqual(1, blockWriter.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0));
        Assert.AreEqual("mftlib-scan-C", blockWriter.LastSectionName);
    }

    [TestMethod]
    public async Task ServeOnce_MftRecordBatchSource_StreamsBatchesToBlockSectionWriter()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 0L),
            (_, _, _) =>
            [
                [new MftRecord(1, 0, new MftRecordFields(1, FileAttributes.Archive, 100), "batch1.txt", null)],
                [new MftRecord(2, 0, new MftRecordFields(1, FileAttributes.Archive, 200), "batch2.txt", null)]
            ],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-streaming-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var blockWriter = CreateSectionWriter();
        await host.ServeAsync(serverSide, blockWriter, true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanProgress));
        var scanReady = frames.Single(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.AreEqual(3L, scanReady.RowCount);
        Assert.AreEqual("batch1.txt", NamePool.ReadRowName(blockWriter.Block, 1).ToString());
        Assert.AreEqual("batch2.txt", NamePool.ReadRowName(blockWriter.Block, 2).ToString());
        Assert.AreEqual(2, blockWriter.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0));
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_KeepsTheNamedFile()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, KeepFileNamesGit);

        Assert.AreEqual(2, writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0)); // repo (directory) + .git (named match)
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_KeepFileNameMatch_IsCaseInsensitive()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, KeepFileNamesGitUppercase);

        Assert.AreEqual(2, writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0)); // repo (directory) + .git (matched despite case)
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_NonMatchingFiles_AreDropped()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, KeepFileNamesNonMatching);

        Assert.AreEqual(1, writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0)); // repo (directory) only
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_NullKeepFileNames_YieldsDirectoriesOnly()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, null);

        Assert.AreEqual(1, writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0)); // repo (directory) only
    }

    [TestMethod]
    public async Task ServeOnce_DirectoryIndexProfile_EmptyKeepFileNames_YieldsDirectoriesOnly()
    {
        using var writer = await ServeDirectoryIndexAsync(DirectoryIndexSampleRecords, Array.Empty<string>());

        Assert.AreEqual(1, writer.Block.Rows.ToArray().Count(row => (row.Flags & RowFlags.InUse) != 0)); // repo (directory) only
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
            host.ServeAsync(serverSide, CreateSectionWriter(), true, CancellationToken.None));
        StringAssert.Contains(exception.Message, "99");
    }

    [TestMethod]
    public void ParseScanSpec_FiveFields_CarriesSectionAndProfile()
    {
        var requests = JournalBrokerHost.ParseScanSpecForTest("C:7:100:map-name:0");
        Assert.AreEqual(1, requests.Length);
        Assert.AreEqual(7UL, requests[0].JournalId);
        Assert.AreEqual(100L, requests[0].NextUsn);
        Assert.AreEqual("map-name", requests[0].MmfName);
        Assert.AreEqual(BrokerScanProfile.Full, requests[0].Profile);
    }

    [TestMethod]
    public async Task ServeOnce_DriveFailure_EmitsErrorFrameAndContinues()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => throw new InvalidOperationException("journal wrapped"),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "D:0:0:mftlib-scan-D");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, CreateSectionWriter(), true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.AreEqual("D", frames[0].Drive);
        Assert.AreEqual("journal wrapped", frames[0].Message);
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

        var host = CreateHost(QueryCursor, (_, _, _) => [], ReadJournal);

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C,D:0:0:mftlib-scan-D");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, CreateSectionWriter(), true, CancellationToken.None);
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

        var host = CreateHost(
            QueryCursor,
            (_, _, _) => [],
            (_, _) => throw new InvalidOperationException("journal wrapped"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, CreateSectionWriter(), true, CancellationToken.None);
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
        var host = MakeFakeHost(Array.Empty<MftRecord>(), Array.Empty<UsnJournalEntry>());

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(request);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        // oneShot false: only Shutdown should end the loop.
        await host.ServeAsync(serverSide, CreateSectionWriter(), false, CancellationToken.None);
        await serverSide.DisposeAsync();

        Assert.AreEqual(0, ReadAllFrames(clientSide).Count);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void RealBlockSectionWriter_WritesBlock_ClientCanReadItBack()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var sectionName = NamedBlockSection.BuildSectionName('C');
        var (block, lifetime) = NamedBlockSection.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"broker-block-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 128,
            NamePoolCapacity = 1024,
            DeleteOnClose = true
        }, sectionName);
        using (block)
        using (lifetime)
        {
            var cursor = new UsnJournalCursor(7, 100);
            var written = new RealBlockSectionWriter().Write(sectionName, cursor,
                [[new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                    new MftRecord(100, 5, new MftRecordFields(1, FileAttributes.Normal, 2048), "nöte.txt", null)]],
                MftBlockRowFilter.Full, null, CancellationToken.None);

            Assert.AreEqual(101L, written.RowCount);
            Assert.AreEqual(18L, written.NamePoolUsedBytes);
            Assert.AreEqual(0L, written.SkippedRecordCount);
            Assert.IsTrue(block.Header.IsComplete);
            Assert.AreEqual(cursor.JournalId, block.Header.UsnJournalId);
            Assert.AreEqual(cursor.NextUsn, block.Header.UsnNextUsn);
            Assert.AreEqual("nöte.txt", NamePool.ReadRowName(block, 100).ToString());
            Assert.AreEqual(2048L, block.Rows[100].Size);
        }
    }
}
