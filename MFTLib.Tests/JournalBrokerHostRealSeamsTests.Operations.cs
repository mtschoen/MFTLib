using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

public partial class JournalBrokerHostRealSeamsTests
{
    [TestMethod]
    public async Task ServeAsync_ScanAndCatchUp_UseRealMftVolumeSeams()
    {
        var queryInfo = new UsnJournalInfoNative { JournalId = 0xABCD, NextUsn = 5000 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreeNameRecordsResult();
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _, _) => parsePtr;
        MockFreeMftResult();

        using var writer = new RecordingBlockSectionWriter();
        var frames = await ServeDefaultScanAsync(writer);

        var cursor = frames.Single(frame => frame.Kind == BrokerFrameKind.Cursor).Cursor;
        Assert.AreEqual(0xABCDUL, cursor.JournalId);
        Assert.AreEqual(5000L, cursor.NextUsn);
        Assert.AreEqual(RowFlags.InUse, writer.Block.Rows[100].Flags);
        Assert.AreEqual(RowFlags.None, writer.Block.Rows[101].Flags);
        Assert.AreEqual(RowFlags.None, writer.Block.Rows[102].Flags);
        Assert.AreEqual("file0.txt", NamePool.ReadRowName(writer.Block, 100).ToString());
        var catchUp = frames.Single(frame => frame.Kind == BrokerFrameKind.ScanCompleted);
        Assert.AreEqual(0, catchUp.Entries.Length);
        Assert.AreEqual(5100L, catchUp.Cursor.NextUsn);
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_AdaptsNativeProgressWithoutPathResolution()
    {
        var queryInfo = new UsnJournalInfoNative { JournalId = 7UL, NextUsn = 100 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreeNameRecordsResult();
        MFTLibNative._parseMftRecordsWithProgress = (_, _, flags, _, _, callback) =>
        {
            Assert.AreEqual(MatchFlags.None, flags);
            callback?.Invoke(MftScanPhase.Parsing, 200, 300, 42.0, IntPtr.Zero);
            callback?.Invoke(MftScanPhase.Parsing, 300, 300, 45.0, IntPtr.Zero);
            return parsePtr;
        };
        MockFreeMftResult();

        using var writer = new RecordingBlockSectionWriter();
        var frames = await ServeDefaultScanAsync(writer);
        var reports = frames.Where(frame => frame.Kind == BrokerFrameKind.ScanProgress)
            .Select(frame => frame.Progress!.Value).ToArray();

        // Intermediate parsing reports may be coalesced before the pump reads them. The final
        // report must retain native totals, independent of the three written rows.
        Assert.AreEqual(300L, reports[^1].RecordsProcessed);
        Assert.AreEqual(300L, reports[^1].TotalRecords);
        Assert.AreEqual(BrokerScanPhase.Transferring, reports[^1].Phase);
        Assert.IsTrue(writer.Block.Header.IsComplete);
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_UsesRealWatchAndDisposeSeam()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        FileUtilities._getWatchVolumeHandle = _ => FakeHandle();
        MockWatchJournalTip();

        // The watch's third read blocks like a live kernel wait until the watch is cancelled.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        MFTLibNative._watchUsnJournalBatchCancelable = (_, startUsn, journalId, _) =>
        {
            switch (Interlocked.Increment(ref callCount))
            {
                case 1:
                    return BuildEmptyWatchResult(journalId, startUsn);
                case 2:
                    return BuildSingleEntryWatchResult(journalId, startUsn + 100, "watched.txt", 0x100 /* FileCreate */);
                default:
                    cancelled.Task.Wait(HostChannelHarness.HangGuard);
                    return BuildEmptyWatchResult(journalId, startUsn);
            }
        };
        MFTLibNative._cancelUsnJournalWatch = _ =>
        {
            cancelled.TrySetResult();
            return true;
        };
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());

        // The cached cursor precedes the queried tip, so the batch must precede CaughtUp.
        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 100));

        var batch = await HostChannelHarness.ReadFrameAsync(pipe);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch?.Kind);
        Assert.AreEqual("watched.txt", batch!.Value.Entries[0].FileName);
        var caughtUp = await HostChannelHarness.ReadFrameAsync(pipe);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp?.Kind);

        await pipe.DisposeAsync(); // the client closing its pipe cancels the watch
        await cancelled.Task.WaitAsync(HostChannelHarness.HangGuard); // closing the pipe cancels the native watch
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_CancelledBetweenEmptyBatches_EndsWatchCleanly()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        FileUtilities._getWatchVolumeHandle = _ => FakeHandle();
        MockWatchJournalTip();

        // The kernel wait comes back only when the watch is cancelled, with an empty batch while
        // already cancelled. MftVolume.WatchUsnJournal treats that as a clean end, distinct
        // from a cancelled Task.Run throwing OperationCanceledException.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MFTLibNative._watchUsnJournalBatchCancelable = (_, startUsn, journalId, _) =>
        {
            watchEntered.TrySetResult();
            cancelled.Task.Wait(HostChannelHarness.HangGuard);
            return BuildEmptyWatchResult(journalId, startUsn);
        };
        MFTLibNative._cancelUsnJournalWatch = _ =>
        {
            cancelled.TrySetResult();
            return true;
        };
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault());
        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7, 100));
        await watchEntered.Task.WaitAsync(HostChannelHarness.HangGuard);

        // Ending the session cancels the channel while the read is blocked.
        await harness.CloseControlAsync();
        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error),
            "A watch cancelled between empty batches ends without reporting a failure.");
    }

    [TestMethod]
    public async Task ServeAsync_DefaultSource_IncludesRootDirectoryRow()
    {
        var queryInfo = new UsnJournalInfoNative { JournalId = 0x1234, NextUsn = 1000 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

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
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(parseResult, parsePtr, false);

            MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _, _) => parsePtr;
            MockFreeMftResult();
        }

        using var writer = new RecordingBlockSectionWriter();
        await ServeDefaultScanAsync(writer);
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, writer.Block.Rows[5].Flags);
        Assert.AreEqual(5u, writer.Block.Rows[5].ParentRow);
        Assert.AreEqual(".", NamePool.ReadRowName(writer.Block, 5).ToString());
        Assert.AreEqual(RowFlags.InUse | RowFlags.Directory, writer.Block.Rows[100].Flags);
        Assert.AreEqual(5u, writer.Block.Rows[100].ParentRow);
        Assert.AreEqual("Users", NamePool.ReadRowName(writer.Block, 100).ToString());
    }

    /// <summary>
    ///     Owns the CountdownEvent and per-drive cancellation signals for
    ///     <see cref="ServeAsync_SessionEnd_AcrossSeveralWatchChannels_SynchronousAbort_EmitsZeroErrorFrames" />.
    ///     Exposing the seam behavior as instance methods, assigned by method group rather than by an inline
    ///     lambda, means the native delegates never directly close over the CountdownEvent that method disposes.
    ///     Production cancels each watched drive's native read independently: <c>MftVolume.Journal.cs</c> opens a
    ///     dedicated watch handle per drive and its cancellation registration calls
    ///     <c>MFTLibNative._cancelUsnJournalWatch</c> with that drive's own handle only, never a sibling's. The
    ///     cancellation signal here is keyed the same way, by the <see cref="SafeHandle" /> instance each drive
    ///     was watched with (<see cref="FakeHandle" /> returns a distinct instance per call even though every
    ///     instance wraps the same raw value 1, so reference identity is a valid key). Releasing all three drives
    ///     off one shared signal let a drive whose own token had not yet flipped
    ///     <c>CancellationToken.IsCancellationRequested</c> unblock on a sibling's cancel, loop back, and call
    ///     the watch read a second time after the countdown had already reached zero, throwing
    ///     <see cref="InvalidOperationException" /> out of the watch loop and surfacing as an
    ///     Error frame instead of the deliberate stop's normal empty stream end.
    /// </summary>
    sealed class SynchronousAbortWatchSeam : IDisposable
    {
        readonly CountdownEvent _watchEntered;

        readonly ConcurrentDictionary<SafeHandle, TaskCompletionSource> _cancelSignaledByHandle =
            new(ReferenceEqualityComparer.Instance);

        volatile bool _timedOut;

        public SynchronousAbortWatchSeam(int driveCount)
        {
            _watchEntered = new CountdownEvent(driveCount);
        }

        public bool CancelUsnJournalWatch(SafeHandle volumeHandle)
        {
            GetCancelSignal(volumeHandle).TrySetResult();
            return true;
        }

        public IntPtr WatchUsnJournalBatchCancelable(SafeHandle volumeHandle, long startUsn, ulong journalId,
            SafeHandle cancellationHandle)
        {
            _watchEntered.Signal();
            // Simulate the kernel wait during live watch until this drive's own cancel arrives.
            GetCancelSignal(volumeHandle).Task.GetAwaiter().GetResult();
            // Return the synchronous ERROR_OPERATION_ABORTED result:
            // an empty result with original cursor untouched and no error message.
            return BuildEmptyWatchResult(journalId, startUsn);
        }

        public void WaitForAllDrivesEntered(CancellationToken token)
        {
            _watchEntered.Wait(token);
        }

        // Safety net for the outer test timeout: releases every drive currently blocked, and
        // marks future arrivals so a drive that has not called the watch read yet does not block
        // forever on a signal this method could not have created early.
        public void CancelOnTimeout()
        {
            _timedOut = true;
            foreach (var signal in _cancelSignaledByHandle.Values)
            {
                signal.TrySetCanceled();
            }
        }

        TaskCompletionSource GetCancelSignal(SafeHandle volumeHandle)
        {
            var signal = _cancelSignaledByHandle.GetOrAdd(volumeHandle,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_timedOut)
            {
                signal.TrySetCanceled();
            }

            return signal;
        }

        public void Dispose()
        {
            _watchEntered.Dispose();
        }
    }

    [TestMethod]
    public async Task ServeAsync_GrowUsnJournal_UsesRealMftVolumeSeams()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        // The pre-check reports the journal smaller than the request, so the grow is
        // issued; the post-change read returns the grown sizing.
        var preChange = BuildJournalInfoPointer(maximumSize: 0x200000, allocationDelta: 0x100000);
        var postChange = BuildJournalInfoPointer(maximumSize: 0x400000, allocationDelta: 0x200000);
        var queryCount = 0;
        uint? capturedIoctl = null;
        long capturedMaximum = 0;
        long capturedDelta = 0;

        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            capturedIoctl = ioControlCode;
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            capturedDelta = Marshal.ReadInt64(inBuffer, 8);
            bytesReturned = 0;
            return true;
        }

        try
        {
            MFTLibNative._queryUsnJournal = _ => ++queryCount == 1 ? preChange : postChange;
            MFTLibNative._freeUsnJournalInfo = _ => { }; // the two buffers are freed by this test
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
        finally
        {
            Marshal.FreeHGlobal(preChange);
            Marshal.FreeHGlobal(postChange);
        }
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

        FileUtilities._getVolumeHandle = _ => FakeHandle();
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

    static IntPtr BuildJournalInfoPointer(ulong maximumSize, ulong allocationDelta)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(new UsnJournalInfoNative
        {
            JournalId = 7,
            NextUsn = 200,
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        }, pointer, false);
        return pointer;
    }

    [TestMethod]
    public async Task ServeAsync_SessionEnd_AcrossSeveralWatchChannels_SynchronousAbort_EmitsZeroErrorFrames()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        FileUtilities._getWatchVolumeHandle = _ => FakeHandle();
        MockWatchJournalTip();

        const int driveCount = 3;
        var seam = new SynchronousAbortWatchSeam(driveCount);
        try
        {
            MFTLibNative._cancelUsnJournalWatch = seam.CancelUsnJournalWatch;
            MFTLibNative._watchUsnJournalBatchCancelable = seam.WatchUsnJournalBatchCancelable;
            MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var timeoutRegistration = cts.Token.Register(seam.CancelOnTimeout);
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

            // Ensure all drives are waiting inside the native watch seam before the session ends.
            seam.WaitForAllDrivesEntered(cts.Token);

            // Ending the session cancels every armed channel at once.
            await harness.CloseControlAsync();
            await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);

            foreach (var pipe in pipes)
            {
                var frames = await HostChannelHarness.ReadToEndAsync(pipe);
                var errorPayloads = string.Join("; ",
                    frames.Where(frame => frame.Kind == BrokerFrameKind.Error).Select(frame => frame.Message));
                Assert.AreEqual(0, frames.Count,
                    $"Expected the channel to end without a frame after its watch was stopped. Errors: {errorPayloads}");
            }
        }
        finally
        {
            seam.Dispose();
        }
    }
}
