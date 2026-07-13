using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests;

/// <summary>
/// Exercises the real production delegates <see cref="JournalBrokerHost.CreateDefault"/>
/// wires up (MftVolume-backed query/scan/catch-up/watch), using the same non-admin
/// native-mock technique as MockVolumeTests / UsnJournalTests, instead of the fake
/// delegates JournalBrokerHostTests injects directly.
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

    static SafeFileHandle FakeHandle() => new(new IntPtr(1), ownsHandle: false);

    // Three path-entry records: [0] kept (in use, real path), [1] skipped (not in
    // use), [2] skipped (in use but an empty path) - covers both operands of
    // ToScanRecords' `!record.InUse || string.IsNullOrEmpty(record.FullPath)` filter.
    static unsafe IntPtr BuildThreePathRecordsResult()
    {
        var stride = MftResult.NativePathEntrySize;
        var entryBuf = Marshal.AllocHGlobal(stride * 3);
        new Span<byte>((void*)entryBuf, stride * 3).Clear();

        var kept = (byte*)entryBuf;
        Unsafe.WriteUnaligned(kept, 100UL);
        Unsafe.WriteUnaligned(kept + 8, 5UL);
        Unsafe.WriteUnaligned(kept + 16, (ushort)1); // InUse, not directory
        var keptPath = "dir\\file0.txt";
        Unsafe.WriteUnaligned(kept + 18, (ushort)keptPath.Length);
        keptPath.AsSpan().CopyTo(new Span<char>(kept + MftResult.NativeStringOffset, keptPath.Length));

        var notInUse = (byte*)entryBuf + stride;
        Unsafe.WriteUnaligned(notInUse, 101UL);
        Unsafe.WriteUnaligned(notInUse + 8, 5UL);
        Unsafe.WriteUnaligned(notInUse + 16, (ushort)0); // not in use
        var skipPath = "dir\\skip.txt";
        Unsafe.WriteUnaligned(notInUse + 18, (ushort)skipPath.Length);
        skipPath.AsSpan().CopyTo(new Span<char>(notInUse + MftResult.NativeStringOffset, skipPath.Length));

        var emptyPath = (byte*)entryBuf + 2 * stride;
        Unsafe.WriteUnaligned(emptyPath, 102UL);
        Unsafe.WriteUnaligned(emptyPath + 8, 5UL);
        Unsafe.WriteUnaligned(emptyPath + 16, (ushort)1); // in use, but zero-length path
        Unsafe.WriteUnaligned(emptyPath + 18, (ushort)0);

        var result = new MftParseResult
        {
            TotalRecords = 3,
            UsedRecords = 3,
            Entries = IntPtr.Zero,
            PathEntries = entryBuf,
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    [TestMethod]
    public void ArmAndScanAndCatchUp_UseRealMftVolumeSeams()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();

        var queryInfo = new UsnJournalInfoNative { JournalId = 0xABCD, NextUsn = 5000 };
        var queryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(queryInfo, queryPtr, false);
        MFTLibNative.QueryUsnJournal = _ => queryPtr;
        MFTLibNative.FreeUsnJournalInfo = _ => Marshal.FreeHGlobal(queryPtr);

        var parsePtr = BuildThreePathRecordsResult();
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => parsePtr;
        MFTLibNative.FreeMftResult = ptr =>
        {
            var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parsed.PathEntries != IntPtr.Zero) Marshal.FreeHGlobal(parsed.PathEntries);
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
            JournalId = 0xABCD,
        };
        var readPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(readResult, readPtr, false);
        MFTLibNative.ReadUsnJournal = (_, _, _) => readPtr;
        MFTLibNative.FreeUsnJournalResult = _ => Marshal.FreeHGlobal(readPtr);

        var (entries, updated) = host.CatchUp("C", cursor);
        Assert.AreEqual(0, entries.Length);
        Assert.AreEqual(5100L, updated.NextUsn);
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_UsesRealWatchAndDisposeSeam()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();

        var callCount = 0;
        MFTLibNative.WatchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            callCount++;
            return callCount == 1
                ? BuildEmptyWatchResult(journalId, startUsn)
                : BuildSingleEntryWatchResult(journalId, startUsn + 100, "watched.txt", reason: 0x100 /* FileCreate */);
        };
        MFTLibNative.CancelUsnJournalWatch = _ => true;
        MFTLibNative.FreeUsnJournalResult = ptr => Marshal.FreeHGlobal(ptr);

        var host = JournalBrokerHost.CreateDefault();
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        // A non-zero JournalId means the host uses this cursor directly instead of
        // calling queryCursor (which would need MFTLibNative.QueryUsnJournal mocked
        // too) - keeps this test focused on the watch seam.
        var request = new System.Buffers.ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frame.Kind);
        Assert.AreEqual("watched.txt", frame.Entries[0].FileName);

        cts.Cancel();
        await serveTask; // ServeAsync swallows OperationCanceledException internally
    }

    [TestMethod]
    public async Task ServeAsync_StartWatch_CancelledBetweenEmptyBatches_EndsWatchCleanly()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        // Not a `using var`: the token is captured by the WatchUsnJournalBatch mock
        // below, so it is disposed explicitly at the end instead - safe because that
        // Dispose() runs only after ServeAsync (which drives the mock) completes.
        var cts = new CancellationTokenSource();

        MFTLibNative.WatchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            // Simulate cancellation racing the kernel wait: cancel, then return an
            // empty batch. MftVolume.WatchUsnJournalWithCursor treats "empty batch +
            // already cancelled" as a clean `yield break`, distinct from a cancelled
            // Task.Run throwing OperationCanceledException.
            // aislop-ignore-next-line AccessToDisposedClosure
            cts.Cancel();
            return BuildEmptyWatchResult(journalId, startUsn);
        };
        MFTLibNative.CancelUsnJournalWatch = _ => true;
        MFTLibNative.FreeUsnJournalResult = ptr => Marshal.FreeHGlobal(ptr);

        var host = JournalBrokerHost.CreateDefault();
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var request = new System.Buffers.ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, "C:7:100");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        // ServeAsync's own token is the same source: once the watch ends cleanly,
        // the outer serve loop unwinds too (its blocked read gets cancelled).
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), oneShot: false, cts.Token);
        cts.Dispose();
    }

    static IntPtr BuildEmptyWatchResult(ulong journalId, long nextUsn)
    {
        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 0,
            Entries = IntPtr.Zero,
            NextUsn = nextUsn,
            JournalId = journalId,
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
            JournalId = journalId,
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPtr, false);
        return resultPtr;
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }
}
