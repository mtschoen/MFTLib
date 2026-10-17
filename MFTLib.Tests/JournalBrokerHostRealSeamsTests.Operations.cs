using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

public partial class JournalBrokerHostRealSeamsTests
{
    [TestMethod]
    public async Task ServeAsync_ScanAndCatchUp_UseRealMftVolumeSeams()
    {
        JournalQueryFake.Serve(journalIdentifier: 0xABCD, nextUsn: 5000);

        var parsePtr = BuildThreeNameRecordsResult();
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _) => parsePtr;
        MockFreeMftResult();

        using var writer = new RecordingBlockSectionWriter();
        var frames = await ServeDefaultScanAsync(writer, 5000);

        var cursor = frames.Single(frame => frame.Kind == BrokerFrameKind.Cursor).Cursor;
        Assert.AreEqual(0xABCDUL, cursor.JournalIdentifier);
        Assert.AreEqual(5000L, cursor.NextUsn);
        Assert.AreEqual(RowFlags.InUse, writer.Block.Rows[100].Flags);
        Assert.AreEqual(RowFlags.None, writer.Block.Rows[101].Flags);
        Assert.AreEqual(RowFlags.None, writer.Block.Rows[102].Flags);
        Assert.AreEqual("file0.txt", NamePool.ReadRowName(writer.Block, 100).ToString());
        var catchUp = frames.Single(frame => frame.Kind == BrokerFrameKind.ScanCompleted);
        Assert.AreEqual(0, catchUp.Entries.Length);
        Assert.AreEqual(default(UsnJournalCursor), catchUp.Cursor);
        Assert.AreEqual(cursor.JournalIdentifier, writer.Block.Header.UsnJournalIdentifier);
        Assert.AreEqual(cursor.NextUsn, writer.Block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_AdaptsNativeProgressWithoutFreedRows()
    {
        JournalQueryFake.Serve(journalIdentifier: 7UL, nextUsn: 100);

        var parsePtr = BuildThreeNameRecordsResult();
        var freedRequests = new List<bool>();
        MFTLibNative._parseMftRecordsWithProgress = (_, freed, _, _, callback) =>
        {
            freedRequests.Add(freed);
            callback?.Invoke(200, 300, 42.0);
            callback?.Invoke(300, 300, 45.0);
            return parsePtr;
        };
        MockFreeMftResult();

        using var writer = new RecordingBlockSectionWriter();
        var frames = await ServeDefaultScanAsync(writer, 100);
        var reports = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress)
            .Select(frame => frame.Progress!.Value).ToArray();

        // Intermediate parsing reports may be coalesced before the pump reads them. The final
        // report must retain native totals, independent of the three written rows.
        Assert.AreEqual(300L, reports[^1].RecordsProcessed);
        Assert.AreEqual(300L, reports[^1].TotalRecords);
        Assert.AreEqual(BrokerScanPhase.Transferring, reports[^1].Phase);
        Assert.IsTrue(writer.Block.Header.IsComplete);
        CollectionAssert.AreEqual(new[] { false }, freedRequests, "The default scan asks for no freed rows.");
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_ClosingThePipeCancelsTheNativeWatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The native USN watch hooks are Windows-only.");
        }

        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        MockWatchJournalTip();

        // The watch's first two reads are queued replies; its third parks on the idle pipe like a
        // live kernel wait until the watch is cancelled.
        using var idlePipe = await IdleUsnPipe.CreateAsync();
        QueuedUsnReads.Queue(UsnReadBuffers.Build(100));
        QueuedUsnReads.Queue(UsnReadBuffers.Build(200, (42, 5, 150, 0x100 /* FileCreate */, "watched.txt")));
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        // The cached cursor precedes the queried tip, so the batch must precede CaughtUp.
        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 100));

        var batch = await HostChannelHarness.ReadFrameAsync(pipe);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch?.Kind);
        Assert.AreEqual("watched.txt", batch!.Value.Entries[0].FileName);
        var caughtUp = await HostChannelHarness.ReadFrameAsync(pipe);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp?.Kind);

        await pipe.DisposeAsync(); // the client closing its pipe cancels the watch
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.CancelAttempted); // closing the pipe cancels the native watch
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_CancelledBetweenEmptyBatches_EndsWatchCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The native USN watch hooks are Windows-only.");
        }

        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        MockWatchJournalTip();

        // The kernel wait comes back only when the watch is cancelled, with an empty batch while
        // already cancelled. UsnJournalVolume.WatchUsnJournal treats that as a clean end, distinct
        // from a cancelled Task.Run throwing OperationCanceledException.
        using var idlePipe = await IdleUsnPipe.CreateAsync(gateReadNumber: 1);
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());
        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 100));
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.BeforeIssue);
        idlePipe.ContinueIssue.Set();
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.Issued);

        // Ending the session cancels the channel while the read is pending.
        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HangGuard);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error),
            "A watch cancelled between empty batches ends without reporting a failure.");
        var calls = IdleUsnPipe.CallOrder();
        Assert.IsTrue(calls.Contains('C') && calls.Contains('F'), "The cancelled read is cancelled and freed.");
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_IncludesRootDirectoryRow()
    {
        JournalQueryFake.Serve(journalIdentifier: 0x1234, nextUsn: 1000);

        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride * 2);
        unsafe
        {
            new Span<byte>((void*)entryBuf, (int)stride * 2).Clear();

            var childName = "Users";
            var stringBuf = Marshal.AllocHGlobal(childName.Length * sizeof(char));
            childName.AsSpan().CopyTo(new Span<char>((void*)stringBuf, childName.Length));

            // Record 5: root directory (empty native name falls back to dot)
            var rootEntry = (byte*)entryBuf;
            Unsafe.WriteUnaligned(rootEntry, 5UL);
            Unsafe.WriteUnaligned(rootEntry + 8, 5UL);
            Unsafe.WriteUnaligned(rootEntry + 16, 0UL);
            Unsafe.WriteUnaligned(rootEntry + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(rootEntry + 28, (ushort)3); // InUse | Directory
            Unsafe.WriteUnaligned(rootEntry + 30, (ushort)0); // zero-length name

            // Record 100: child folder
            var childEntry = (byte*)entryBuf + stride;
            Unsafe.WriteUnaligned(childEntry, 100UL);
            Unsafe.WriteUnaligned(childEntry + 8, 5UL);
            Unsafe.WriteUnaligned(childEntry + 16, 0UL);
            Unsafe.WriteUnaligned(childEntry + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(childEntry + 28, (ushort)3); // InUse | Directory
            Unsafe.WriteUnaligned(childEntry + 30, (ushort)childName.Length);

            var parseResult = new MftParseResult
            {
                TotalRecords = 2,
                UsedRecords = 2,
                Entries = entryBuf,
                EntryStrings = stringBuf,
                EntryStringUnits = (ulong)childName.Length,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(parseResult, parsePtr, false);

            MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _) => parsePtr;
            MockFreeMftResult();
        }

        using var writer = new RecordingBlockSectionWriter();
        await ServeDefaultScanAsync(writer, 1000);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, writer.Block.Rows[5].Flags);
        Assert.AreEqual(5u, writer.Block.Rows[5].ParentRow);
        Assert.AreEqual(".", NamePool.ReadRowName(writer.Block, 5).ToString());
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, writer.Block.Rows[100].Flags);
        Assert.AreEqual(5u, writer.Block.Rows[100].ParentRow);
        Assert.AreEqual("Users", NamePool.ReadRowName(writer.Block, 100).ToString());
    }

    [TestMethod]
    public async Task ServeAsync_GrowUsnJournal_UsesRealMftVolumeSeams()
    {
        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();

        // The pre-check reports the journal smaller than the request, so the grow is
        // issued; the post-change read returns the grown sizing.
        var preChange = JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000);
        var postChange = JournalQueryFake.Describe(7, 200, maximumSize: 0x400000, allocationDelta: 0x200000);
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1 ? preChange : postChange);
        uint? capturedIoctl = null;
        long capturedMaximum = 0;
        long capturedDelta = 0;

        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }
            Assert.AreEqual(0x000900E7u, ioControlCode);
            capturedIoctl = ioControlCode;
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            capturedDelta = Marshal.ReadInt64(inBuffer, 8);
            bytesReturned = 0;
            return true;
        }

        Kernel32._deviceIoControl = FakeDeviceIoControl;
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        await harness.SendControlAsync(writer =>
            BrokerProtocol.WriteGrowUsnJournal(writer, 6, "C", 0x400000, 0x200000));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual(6u, frame.RequestId);
        Assert.AreEqual(0x400000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x200000L, frame.JournalAllocationDelta);
        Assert.AreEqual(0x000900E7u, capturedIoctl, "FSCTL_CREATE_USN_JOURNAL");
        Assert.AreEqual(0x400000L, capturedMaximum);
        Assert.AreEqual(0x200000L, capturedDelta);
        Assert.AreEqual(2, queryCount, "The grow reads the sizing before and after the change.");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only
    public async Task ServeAsync_QueryVolume_UsesRealNtfsVolumeInformationSeam()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("FSCTL_GET_NTFS_VOLUME_DATA requires Windows");
            return;
        }

        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var native = new NtfsVolumeDataBufferNative
        {
            MftValidDataLength = 409_600,
            BytesPerFileRecordSegment = 1024
        };
        Kernel32._deviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
        {
            Marshal.StructureToPtr(native, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 3, "C"));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, frame.Kind);
        Assert.AreEqual(3u, frame.RequestId);
        Assert.AreEqual(409_600L, frame.MftValidDataLength);
        Assert.AreEqual(1024u, frame.BytesPerFileRecordSegment);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ServeAsync_SessionEnd_AcrossSeveralWatchChannels_EmitsZeroErrorFrames(bool cancelDrivesIndividually)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The native USN watch hooks are Windows-only.");
        }

        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        MockWatchJournalTip();

        // Every drive's watch reads its own idle pipe; the third read to start is the gate.
        const int driveCount = 3;
        using var pipeC = await IdleUsnPipe.CreateAsync();
        using var pipeD = await IdleUsnPipe.CreateAsync();
        using var idlePipe = await IdleUsnPipe.CreateAsync(driveCount);
        var idlePipes = new Dictionary<string, IdleUsnPipe>
        {
            [@"\\.\C:"] = pipeC,
            [@"\\.\D:"] = pipeD,
            [@"\\.\E:"] = idlePipe
        };
        FileUtilities._getWatchVolumeHandle = volumePath => idlePipes[volumePath].BorrowHandle();
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        // Watch several drives with cursors at or beyond the journal tip (7:200).
        var pipes = new List<Stream>
        {
            await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 200)),
            await harness.OpenWatchChannelAsync('D', new UsnJournalCursor(7, 200)),
            await harness.OpenWatchChannelAsync('E', new UsnJournalCursor(7, 300))
        };

        // Verify each drive reaches CaughtUp.
        foreach (var pipe in pipes)
        {
            var frame = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.CaughtUp, frame?.Kind);
        }

        // Ensure all drives are waiting inside the native watch before the session ends.
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.BeforeIssue);
        idlePipe.ContinueIssue.Set();
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.Issued);

        if (cancelDrivesIndividually)
        {
            // Each drive's read stays pending until its own cancel: closing one client pipe cancels only that watch.
            foreach (var (pipe, own) in new[] { (pipes[0], pipeC), (pipes[1], pipeD) })
            {
                await pipe.DisposeAsync();
                await IdleUsnPipe.AwaitSignalAsync(own.CancelAttempted);
                pipes.Remove(pipe);
                Assert.AreEqual(driveCount - pipes.Count, IdleUsnPipe.CallOrder().Count(call => call == 'C'));
                Assert.IsFalse(idlePipe.CancelAttempted.WaitOne(0), "A sibling's cancel must not reach this drive's read.");
            }
        }

        // Ending the session cancels every channel still armed.
        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HangGuard);
        Assert.IsTrue(idlePipe.CancelAttempted.WaitOne(0));
        Assert.AreEqual(driveCount, IdleUsnPipe.CallOrder().Count(call => call == 'C'));

        foreach (var pipe in pipes)
        {
            var frames = await HostChannelHarness.ReadToEndAsync(pipe);
            var errorPayloads = string.Join("; ",
                frames.Where(frame => frame.Kind == BrokerFrameKind.Error).Select(frame => frame.Message));
            Assert.AreEqual(0, frames.Count,
                $"Expected the channel to end without a frame after its watch was stopped. Errors: {errorPayloads}");
        }
    }
}
