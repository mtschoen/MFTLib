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
        StreamedParse.Install((_, _, _, _, _) => parsePtr);
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
        Assert.IsEmpty(catchUp.Entries);
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
        StreamedParse.Install((_, freed, _, _, callback) =>
        {
            freedRequests.Add(freed);
            callback.Invoke(200, 300, 42.0);
            callback.Invoke(300, 300, 45.0);
            return parsePtr;
        });
        MockFreeMftResult();

        using var writer = new RecordingBlockSectionWriter();
        var frames = await ServeDefaultScanAsync(writer, 100);
        var reports = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress)
            .Select(frame => frame.Progress!.Value).ToArray();

        // The bounded channel may coalesce every scanning report before the pump reads it. Any
        // emitted scanning report retains native totals, independent of the three written rows;
        // the mandatory Indexing sample is the last frame before ScanReady.
        var scanning = reports.Where(report => report.Phase == BlockScanPhase.Scanning).ToArray();
        if (scanning.Length > 0)
        {
            Assert.AreEqual(300L, scanning[^1].RecordsProcessed);
            Assert.AreEqual(300L, scanning[^1].TotalRecords);
        }
        Assert.AreEqual(BlockWriteProgress.Indexing, reports[^1]);
        Assert.IsTrue(writer.Block.Header.IsComplete);
        Assert.AreSequenceEqual([false], freedRequests, "The default scan asks for no freed rows.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ServeAsync_StartWatch_ClosingThePipeCancelsTheNativeWatch()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
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
    [OSCondition(OperatingSystems.Windows)]
    public async Task ServeAsync_StartWatch_CancelledBetweenEmptyBatches_EndsWatchCleanly()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
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
        await harness.Serve.WaitAsync(HangGuard, TestContext.CancellationToken);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.Error, frames,
            "A watch cancelled between empty batches ends without reporting a failure.");
        var calls = IdleUsnPipe.CallOrder();
        Assert.IsTrue(calls.Contains('C') && calls.Contains('F'), "The cancelled read is cancelled and freed.");
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_IncludesRootDirectoryRow()
    {
        JournalQueryFake.Serve(journalIdentifier: 0x1234, nextUsn: 1000);

        const nuint stride = MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride * 2);
        unsafe
        {
            new Span<byte>((void*)entryBuf, (int)stride * 2).Clear();

            const string childName = "Users";
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

            StreamedParse.Install((_, _, _, _, _) => parsePtr);
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
    [DataRow(0x400000L, UsnJournalGrowthOutcome.GrewAsRequested)]
    [DataRow(0x200000L, UsnJournalGrowthOutcome.Unchanged)]
    public async Task ServeAsync_GrowUsnJournal_UsesRealMftVolumeSeams(
        long postChangeMaximumBytes,
        UsnJournalGrowthOutcome expectedOutcome)
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = 100UL * 1024 * 1024 * 1024,
                SectorsPerAllocationUnit = 1,
                BytesPerSector = 1
            };
            return 0;
        };

        // The pre-check reports the journal smaller than the request, so the grow is
        // issued; the post-change read classifies whether the volume grew or kept its maximum.
        var preChange = JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000);
        var postChange = JournalQueryFake.Describe(7, 200, maximumSize: (ulong)postChangeMaximumBytes, allocationDelta: 0x200000);
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

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        await harness.SendControlAsync(writer =>
            BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(6, 'C', 0x400000, 0x200000)));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalGrowthResult, frame.Kind);
        Assert.AreEqual(6u, frame.RequestId);
        Assert.AreEqual(expectedOutcome, frame.JournalGrowthOutcome);
        Assert.AreEqual(0x400000L, frame.JournalRequestedMaximumSize);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, frame.JournalMaximumRecommendedSize);
        Assert.AreEqual(postChangeMaximumBytes, frame.JournalMaximumSize);
        Assert.AreEqual(0x200000L, frame.JournalAllocationDelta);
        Assert.AreEqual(0x000900E7u, capturedIoctl, "FSCTL_CREATE_USN_JOURNAL");
        Assert.AreEqual(0x400000L, capturedMaximum);
        Assert.AreEqual(0x200000L, capturedDelta);
        Assert.AreEqual(2, queryCount, "The grow reads the sizing before and after the change.");
    }

    [TestMethod]
    public async Task ServeAsync_GrowUsnJournal_OverCap_ClampsWithoutOverrideAndAppliesFullSizeWithOverride()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = 100UL * 1024 * 1024 * 1024, // 5 GiB cap
                SectorsPerAllocationUnit = 1,
                BytesPerSector = 1
            };
            return 0;
        };
        const long fiveGiB = 5L * 1024 * 1024 * 1024;
        const long clampedRequest = fiveGiB + 1;
        const long overrideRequest = fiveGiB + 0x100000;
        var currentMaximum = 0x200000L;
        var createMaxima = new List<long>();
        var query = JournalQueryFake.Create(() =>
            JournalQueryFake.Describe(7, 200, maximumSize: (ulong)currentMaximum, allocationDelta: 0x100000));
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            var target = Marshal.ReadInt64(inBuffer, 0);
            createMaxima.Add(target);
            currentMaximum = target;
            bytesReturned = 0;
            return true;
        };
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        // 1. Over cap without override: clamps to 5 GiB and reports GrewClampedToMaximumRecommended.
        await harness.SendControlAsync(writer =>
            BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(15, 'C', clampedRequest, 0x100000)));
        var clampedReply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalGrowthResult, clampedReply.Kind);
        Assert.AreEqual(15u, clampedReply.RequestId);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended, clampedReply.JournalGrowthOutcome);
        Assert.AreEqual(clampedRequest, clampedReply.JournalRequestedMaximumSize);
        Assert.AreEqual(fiveGiB, clampedReply.JournalMaximumRecommendedSize);
        Assert.AreEqual(fiveGiB, clampedReply.JournalMaximumSize);
        Assert.AreSequenceEqual([fiveGiB], createMaxima);

        // 2. Over cap again without override when already at the 5 GiB cap: no create IOCTL and AlreadyAtOrAboveTarget.
        await harness.SendControlAsync(writer =>
            BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(16, 'C', overrideRequest, 0x100000)));
        var alreadyAtCapReply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalGrowthResult, alreadyAtCapReply.Kind);
        Assert.AreEqual(16u, alreadyAtCapReply.RequestId);
        Assert.AreEqual(UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget, alreadyAtCapReply.JournalGrowthOutcome);
        Assert.AreEqual(overrideRequest, alreadyAtCapReply.JournalRequestedMaximumSize);
        Assert.AreEqual(fiveGiB, alreadyAtCapReply.JournalMaximumRecommendedSize);
        Assert.AreEqual(fiveGiB, alreadyAtCapReply.JournalMaximumSize);
        Assert.AreSequenceEqual([fiveGiB], createMaxima, "Already-at-cap clamped request must not issue FSCTL_CREATE_USN_JOURNAL.");

        // 3. Over cap with explicit override: applies the full requested size above 5 GiB.
        await harness.SendControlAsync(writer =>
            BrokerProtocol.WriteGrowUsnJournal(writer,
                BrokerFrame.GrowUsnJournal(17, 'C', overrideRequest, 0x100000, allowAboveMaximumRecommendedSize: true)));
        var overrideReply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalGrowthResult, overrideReply.Kind);
        Assert.AreEqual(17u, overrideReply.RequestId);
        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride, overrideReply.JournalGrowthOutcome);
        Assert.AreEqual(overrideRequest, overrideReply.JournalRequestedMaximumSize);
        Assert.AreEqual(fiveGiB, overrideReply.JournalMaximumRecommendedSize);
        Assert.AreEqual(overrideRequest, overrideReply.JournalMaximumSize);
        Assert.AreSequenceEqual([fiveGiB, overrideRequest], createMaxima);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")] // NtfsVolumeInformation.Query is Windows-only
    public async Task ServeAsync_QueryVolume_UsesRealNtfsVolumeInformationSeam()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var native = new NtfsVolumeDataBufferNative
        {
            MftValidDataLength = 409_600,
            BytesPerFileRecordSegment = 1024
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
        {
            Marshal.StructureToPtr(native, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(3, 'C')));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, frame.Kind);
        Assert.AreEqual(3u, frame.RequestId);
        Assert.AreEqual(409_600L, frame.MftValidDataLength);
        Assert.AreEqual(1024u, frame.BytesPerFileRecordSegment);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ServeAsync_SessionEnd_AcrossSeveralWatchChannels_EmitsZeroErrorFrames(bool cancelVolumesIndividually)
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        MockWatchJournalTip();

        // Every volume's watch reads its own idle pipe; the third read to start is the gate.
        const int volumeCount = 3;
        using var pipeC = await IdleUsnPipe.CreateAsync();
        using var pipeD = await IdleUsnPipe.CreateAsync();
        using var idlePipe = await IdleUsnPipe.CreateAsync(volumeCount);
        var idlePipes = new Dictionary<string, IdleUsnPipe>
        {
            [@"\\.\C:"] = pipeC,
            [@"\\.\D:"] = pipeD,
            [@"\\.\E:"] = idlePipe
        };
        FileUtilities.Hooks._getWatchVolumeHandle = volumePath => idlePipes[volumePath].BorrowHandle();
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        // Watch several volumes with cursors at or beyond the journal tip (7:200).
        var pipes = new List<Stream>
        {
            await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 200)),
            await harness.OpenWatchChannelAsync('D', new UsnJournalCursor(7, 200)),
            await harness.OpenWatchChannelAsync('E', new UsnJournalCursor(7, 300))
        };

        // Verify each volume reaches CaughtUp.
        foreach (var pipe in pipes)
        {
            var frame = await HostChannelHarness.ReadFrameAsync(pipe);
            Assert.AreEqual(BrokerFrameKind.CaughtUp, frame?.Kind);
        }

        // Ensure all volumes are waiting inside the native watch before the session ends.
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.BeforeIssue);
        idlePipe.ContinueIssue.Set();
        await IdleUsnPipe.AwaitSignalAsync(idlePipe.Issued);

        if (cancelVolumesIndividually)
        {
            // Each volume's read stays pending until its own cancel: closing one client pipe cancels only that watch.
            foreach (var (pipe, own) in new[] { (pipes[0], pipeC), (pipes[1], pipeD) })
            {
                await pipe.DisposeAsync();
                await IdleUsnPipe.AwaitSignalAsync(own.CancelAttempted);
                pipes.Remove(pipe);
                Assert.AreEqual(volumeCount - pipes.Count, IdleUsnPipe.CallOrder().Count(call => call == 'C'));
                Assert.IsFalse(idlePipe.CancelAttempted.WaitOne(0), "A sibling's cancel must not reach this volume's read.");
            }
        }

        // Ending the session cancels every channel still armed.
        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsTrue(idlePipe.CancelAttempted.WaitOne(0));
        Assert.AreEqual(volumeCount, IdleUsnPipe.CallOrder().Count(call => call == 'C'));

        foreach (var pipe in pipes)
        {
            var frames = await HostChannelHarness.ReadToEndAsync(pipe);
            var errorPayloads = string.Join("; ",
                frames.Where(frame => frame.Kind == BrokerFrameKind.Error).Select(frame => frame.Message));
            Assert.IsEmpty(frames,
                $"Expected the channel to end without a frame after its watch was stopped. Errors: {errorPayloads}");
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
