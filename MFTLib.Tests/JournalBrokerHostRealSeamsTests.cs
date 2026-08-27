using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

/// <summary>
///     Exercises the real production delegates <see cref="JournalBrokerHost.CreateDefault" />
///     wires up (MftVolume-backed query/scan/catch-up/watch), using the same non-admin
///     native-mock technique as MockVolumeTests / UsnJournalTests, instead of the fake
///     delegates JournalBrokerHostTests injects directly.
/// </summary>
[TestClass]
public class JournalBrokerHostRealSeamsTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    static SafeFileHandle FakeHandle()
    {
        return new SafeFileHandle(new IntPtr(1), false);
    }

    // Three path-entry records: [0] kept (in use, real path), [1] skipped (not in
    // use), [2] skipped (in use but an empty path) - covers both operands of
    // ToScanRecords' `!record.InUse || string.IsNullOrEmpty(record.FullPath)` filter.
    static unsafe IntPtr BuildThreePathRecordsResult()
    {
        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride * 3);
        new Span<byte>((void*)entryBuf, (int)stride * 3).Clear();

        var keptPath = "dir\\file0.txt";
        var skipPath = "dir\\skip.txt";
        var totalUnits = keptPath.Length + skipPath.Length;
        var stringBuf = Marshal.AllocHGlobal(totalUnits * sizeof(char));
        var stringSpan = new Span<char>((void*)stringBuf, totalUnits);
        keptPath.AsSpan().CopyTo(stringSpan);
        skipPath.AsSpan().CopyTo(stringSpan.Slice(keptPath.Length));

        var kept = (byte*)entryBuf;
        Unsafe.WriteUnaligned(kept, 100UL);
        Unsafe.WriteUnaligned(kept + 8, 5UL);
        Unsafe.WriteUnaligned(kept + 16, 0UL); // stringOffset
        Unsafe.WriteUnaligned(kept + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(kept + 28, (ushort)1); // InUse, not directory
        Unsafe.WriteUnaligned(kept + 30, (ushort)keptPath.Length);

        var notInUse = (byte*)entryBuf + stride;
        Unsafe.WriteUnaligned(notInUse, 101UL);
        Unsafe.WriteUnaligned(notInUse + 8, 5UL);
        Unsafe.WriteUnaligned(notInUse + 16, (ulong)keptPath.Length); // stringOffset
        Unsafe.WriteUnaligned(notInUse + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(notInUse + 28, (ushort)0); // not in use
        Unsafe.WriteUnaligned(notInUse + 30, (ushort)skipPath.Length);

        var emptyPath = (byte*)entryBuf + 2 * stride;
        Unsafe.WriteUnaligned(emptyPath, 102UL);
        Unsafe.WriteUnaligned(emptyPath + 8, 5UL);
        Unsafe.WriteUnaligned(emptyPath + 16, (ulong)totalUnits); // stringOffset
        Unsafe.WriteUnaligned(emptyPath + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(emptyPath + 28, (ushort)1); // in use, but zero-length path
        Unsafe.WriteUnaligned(emptyPath + 30, (ushort)0);

        var result = new MftParseResult
        {
            TotalRecords = 3,
            UsedRecords = 3,
            PathEntries = entryBuf,
            PathStrings = stringBuf,
            PathStringUnits = (ulong)totalUnits,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    [TestMethod]
    public void ArmAndScanAndCatchUp_UseRealMftVolumeSeams()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var queryInfo = new UsnJournalInfoNative { JournalId = 0xABCD, NextUsn = 5000 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreePathRecordsResult();
        MFTLibNative._parseMftRecords = (_, _, _, _) => parsePtr;
        MFTLibNative._freeMftResult = ptr =>
        {
            var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parsed.PathEntries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.PathEntries);
            }

            if (parsed.PathStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.PathStrings);
            }

            Marshal.FreeHGlobal(ptr);
        };

        var host = JournalBrokerHost.CreateDefault();
        var (cursor, records) = host.ArmAndScan("C");

        Assert.AreEqual(0xABCDUL, cursor.JournalId);
        Assert.AreEqual(5000L, cursor.NextUsn);
        // Only the "kept" record survives ToScanRecords' InUse/FullPath filter.
        Assert.AreEqual(1, records.Length);
        Assert.AreEqual(100UL, records[0].RecordNumber);
        Assert.AreEqual("C:\\dir\\file0.txt", records[0].Path);
        Assert.IsFalse(records[0].IsDirectory);

        var readResult = new UsnJournalResultNative
        {
            EntryCount = 0,
            Entries = IntPtr.Zero,
            NextUsn = 5100,
            JournalId = 0xABCD
        };
        var readPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(readResult, readPtr, false);
        MFTLibNative._readUsnJournal = (_, _, _) => readPtr;
        MFTLibNative._freeUsnJournalResult = _ => Marshal.FreeHGlobal(readPtr);

        var (entries, updated) = host.CatchUp("C", cursor);
        Assert.AreEqual(0, entries.Length);
        Assert.AreEqual(5100L, updated.NextUsn);
    }

    [TestMethod]
    public void ArmAndScanBatches_RealScanDriveBatches_AdaptsNativeProgressThroughDirectProgressLambda()
    {
        // Exercises the real ScanDriveBatches production delegate (used only by
        // CreateDefault()) with a non-null progress: the DirectProgress<MftScanProgress>
        // lambda that adapts MftScanProgress into MmfWriteProgress and forwards it to
        // the caller-supplied IProgress<MmfWriteProgress>.
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var queryInfo = new UsnJournalInfoNative { JournalId = 7UL, NextUsn = 100 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative._queryUsnJournal = _ => queryPtr;
        MFTLibNative._freeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreePathRecordsResult();
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, callback, context) =>
        {
            callback?.Invoke(2, 3, 42.0, context);
            return parsePtr;
        };
        MFTLibNative._freeMftResult = ptr =>
        {
            var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parsed.PathEntries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.PathEntries);
            }

            if (parsed.PathStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.PathStrings);
            }

            Marshal.FreeHGlobal(ptr);
        };

        var reported = new List<MmfWriteProgress>();
        var progress = new RecordingProgress(reported.Add);

        var host = JournalBrokerHost.CreateDefault();
        var (_, batches) = host.ArmAndScanBatches("C", progress, CancellationToken.None);
        var materialized = batches.ToList();

        // Only the "kept" record survives ToScanRecords' InUse/FullPath filter, so
        // exactly one non-empty batch is yielded.
        Assert.AreEqual(1, materialized.Count);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual(2L, reported[0].RecordsProcessed);
        Assert.AreEqual(3L, reported[0].TotalRecords);
        Assert.AreEqual(0L, reported[0].BytesProcessed);
        Assert.IsNull(reported[0].TotalBytes);
    }

    sealed class RecordingProgress(Action<MmfWriteProgress> handler) : IProgress<MmfWriteProgress>
    {
        public void Report(MmfWriteProgress value)
        {
            handler(value);
        }
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_UsesRealWatchAndDisposeSeam()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

        var callCount = 0;
        MFTLibNative._watchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            callCount++;
            return callCount == 1
                ? BuildEmptyWatchResult(journalId, startUsn)
                : BuildSingleEntryWatchResult(journalId, startUsn + 100, "watched.txt", 0x100 /* FileCreate */);
        };
        MFTLibNative._cancelUsnJournalWatch = _ => true;
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;

        var host = JournalBrokerHost.CreateDefault();
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        // A non-zero JournalId means the host uses this cursor directly instead of
        // calling queryCursor (which would need MFTLibNative._queryUsnJournal mocked
        // too) - keeps this test focused on the watch seam.
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual("watched.txt", frame.Entries[0].FileName);

        await cts.CancelAsync();
        await serveTask; // ServeAsync swallows OperationCanceledException internally
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_CancelledBetweenEmptyBatches_EndsWatchCleanly()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        // Not a `using var`: the token is captured by the WatchUsnJournalBatch mock
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after ServeAsync (which drives the mock) completes.
        var cts = new CancellationTokenSource();
        Action cancel = cts.Cancel;

        MFTLibNative._watchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            // Simulate cancellation racing the kernel wait: cancel, then return an
            // empty batch. MftVolume.WatchUsnJournalWithCursor treats "empty batch +
            // already cancelled" as a clean `yield break`, distinct from a cancelled
            // Task.Run throwing OperationCanceledException.
            cancel();
            return BuildEmptyWatchResult(journalId, startUsn);
        };
        MFTLibNative._cancelUsnJournalWatch = _ => true;
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;

        var host = JournalBrokerHost.CreateDefault();
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);

        // ServeAsync's own token is the same source: once the watch ends cleanly,
        // the outer serve loop unwinds too (its blocked read gets cancelled).
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);
        cts.Dispose();
    }

    static IntPtr BuildEmptyWatchResult(ulong journalId, long nextUsn)
    {
        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 0,
            Entries = IntPtr.Zero,
            NextUsn = nextUsn,
            JournalId = journalId
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPtr, false);
        return resultPtr;
    }

    static unsafe IntPtr BuildSingleEntryWatchResult(ulong journalId, long nextUsn, string fileName, uint reason)
    {
        var entrySize = MftVolume.NativeUsnEntrySize;
        var entriesPtr = Marshal.AllocHGlobal(entrySize);
        new Span<byte>((void*)entriesPtr, entrySize).Clear();

        var ptr = (byte*)entriesPtr;
        *(ulong*)ptr = 42;
        *(ulong*)(ptr + 8) = 5;
        *(long*)(ptr + 16) = nextUsn - 50;
        *(long*)(ptr + 24) = 0;
        *(uint*)(ptr + 32) = reason;
        *(uint*)(ptr + 36) = 0x20;
        *(ushort*)(ptr + 40) = (ushort)fileName.Length;
        fileName.AsSpan().CopyTo(new Span<char>(ptr + 42, fileName.Length));

        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 1,
            Entries = entriesPtr,
            NextUsn = nextUsn,
            JournalId = journalId
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPtr, false);
        return resultPtr;
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

    [TestMethod]
    public void ArmAndScan_IncludesRootDirectoryRecord_InScanResults()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();

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

            var childPath = "Users";
            var stringBuf = Marshal.AllocHGlobal(childPath.Length * sizeof(char));
            childPath.AsSpan().CopyTo(new Span<char>((void*)stringBuf, childPath.Length));

            // Record 5: root directory (zero-length relative path)
            var rootEntry = (byte*)entryBuf;
            Unsafe.WriteUnaligned(rootEntry, 5UL);
            Unsafe.WriteUnaligned(rootEntry + 8, 5UL);
            Unsafe.WriteUnaligned(rootEntry + 16, 0UL);
            Unsafe.WriteUnaligned(rootEntry + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(rootEntry + 28, (ushort)3); // InUse | Directory
            Unsafe.WriteUnaligned(rootEntry + 30, (ushort)0); // zero-length path

            // Record 100: child folder
            var childEntry = (byte*)entryBuf + stride;
            Unsafe.WriteUnaligned(childEntry, 100UL);
            Unsafe.WriteUnaligned(childEntry + 8, 5UL);
            Unsafe.WriteUnaligned(childEntry + 16, 0UL);
            Unsafe.WriteUnaligned(childEntry + 24, (uint)FileAttributes.Directory);
            Unsafe.WriteUnaligned(childEntry + 28, (ushort)3); // InUse | Directory
            Unsafe.WriteUnaligned(childEntry + 30, (ushort)childPath.Length);

            var parseResult = new MftParseResult
            {
                TotalRecords = 2,
                UsedRecords = 2,
                PathEntries = entryBuf,
                PathStrings = stringBuf,
                PathStringUnits = (ulong)childPath.Length,
                AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
                EntryStride = MFTLibNative.NativeCompactEntrySize
            };
            var parsePtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
            Marshal.StructureToPtr(parseResult, parsePtr, false);

            MFTLibNative._parseMftRecords = (_, _, _, _) => parsePtr;
            MFTLibNative._freeMftResult = ptr =>
            {
                var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
                if (parsed.PathEntries != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(parsed.PathEntries);
                }

                if (parsed.PathStrings != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(parsed.PathStrings);
                }

                Marshal.FreeHGlobal(ptr);
            };

            var host = JournalBrokerHost.CreateDefault();
            var (_, records) = host.ArmAndScan("C");

            Assert.AreEqual(2, records.Length);

            var rootRecord = records.FirstOrDefault(r => r.RecordNumber == 5);
            Assert.AreEqual(5UL, rootRecord.RecordNumber);
            Assert.AreEqual(5UL, rootRecord.ParentRecordNumber);
            Assert.IsTrue(rootRecord.IsDirectory);
            Assert.AreEqual(".", rootRecord.Name);
            Assert.AreEqual(@"C:\", rootRecord.Path);

            var childRecord = records.FirstOrDefault(r => r.RecordNumber == 100);
            Assert.AreEqual(100UL, childRecord.RecordNumber);
            Assert.AreEqual(5UL, childRecord.ParentRecordNumber);
            Assert.IsTrue(childRecord.IsDirectory);
            Assert.AreEqual("Users", childRecord.Name);
            Assert.AreEqual(@"C:\Users", childRecord.Path);
        }
    }
}
