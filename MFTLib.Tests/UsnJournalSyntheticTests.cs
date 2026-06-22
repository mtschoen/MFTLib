using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using MFTLib.Interop;

namespace MFTLib.Tests;

/// <summary>
/// Covers usn_journal.cpp WITHOUT admin, by injecting synthetic IOCTL responses
/// and failures through the native test seams (SetUsnIoSuccess / SetUsnIoFailError
/// / SetUsnOverlappedAbort). The native code parses our buffers exactly as it would
/// real kernel output, so the success, record-parse, grow, error, and watch/cancel
/// paths are all exercised with no elevated volume handle.
///
/// These replace the old RequiresAdmin USN tests that opened the live C: volume.
/// The genuinely-live integration smoke lives in UsnJournalLiveTests (LiveVolume).
/// </summary>
[TestClass]
public class UsnJournalSyntheticTests
{
    readonly List<IntPtr> _buffers = [];

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.NativeResetTestState();
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        foreach (var b in _buffers) Marshal.FreeHGlobal(b);
        _buffers.Clear();
    }

    static SafeFileHandle FakeHandle() => new(new IntPtr(1), ownsHandle: false);

    static readonly UsnJournalCursor Cursor = new(0xABCD, 500);

    // Win32 error constants (mirror the native error branches).
    const uint ERROR_HANDLE_EOF = 38;
    const uint ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    const uint ERROR_JOURNAL_NOT_ACTIVE = 1179;
    const uint ERROR_JOURNAL_ENTRY_DELETED = 1181;

    static void UseFakeHandle() => FileUtilities.GetVolumeHandle = _ => FakeHandle();

    // Queues a synthetic IOCTL success buffer; keeps it alive until cleanup.
    unsafe void QueueSuccess(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        _buffers.Add(ptr);
        MFTLibNative.NativeSetUsnIoSuccess((byte*)ptr, (uint)data.Length);
    }

    // A FSCTL_QUERY_USN_JOURNAL output buffer (USN_JOURNAL_DATA_V0: 8 fields × 8 bytes).
    static byte[] BuildQueryBuffer(ulong journalId = 0xABCD, long firstUsn = 0,
        long nextUsn = 5000, long lowestValid = 0, long maxUsn = 1_000_000,
        ulong maxSize = 0x200000, ulong allocDelta = 0x100000)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(journalId); w.Write(firstUsn); w.Write(nextUsn); w.Write(lowestValid);
        w.Write(maxUsn); w.Write(maxSize); w.Write(allocDelta); w.Write(0UL); // V1 pad
        return ms.ToArray();
    }

    // A FSCTL_READ_USN_JOURNAL output buffer: leading int64 nextUsn, then packed
    // USN_RECORD_V2 records.
    static byte[] BuildReadBuffer(long nextUsn,
        params (ulong rec, ulong parent, long usn, uint reason, string name)[] entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(nextUsn);
        foreach (var e in entries) WriteRecord(w, e.rec, e.parent, e.usn, e.reason, e.name);
        return ms.ToArray();
    }

    static void WriteRecord(BinaryWriter w, ulong rec, ulong parent, long usn, uint reason, string name)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        const int fixedLen = 60; // USN_RECORD_V2 header up to FileName[]
        var recordLen = (fixedLen + nameBytes.Length + 7) & ~7; // 8-byte align
        w.Write((uint)recordLen);          // RecordLength
        w.Write((ushort)2);                // MajorVersion
        w.Write((ushort)0);                // MinorVersion
        w.Write(rec);                      // FileReferenceNumber
        w.Write(parent);                   // ParentFileReferenceNumber
        w.Write(usn);                      // Usn
        w.Write(0L);                       // TimeStamp
        w.Write(reason);                   // Reason
        w.Write(0u);                       // SourceInfo
        w.Write(0u);                       // SecurityId
        w.Write(0u);                       // FileAttributes
        w.Write((ushort)nameBytes.Length); // FileNameLength (bytes)
        w.Write((ushort)fixedLen);         // FileNameOffset
        w.Write(nameBytes);
        for (var i = 0; i < recordLen - (fixedLen + nameBytes.Length); i++) w.Write((byte)0);
    }

    // --- QueryUsnJournal ---

    [TestMethod]
    public void QueryUsnJournal_SyntheticSuccess_ReturnsCursor()
    {
        UseFakeHandle();
        QueueSuccess(BuildQueryBuffer(journalId: 0xABCD, nextUsn: 5000));
        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();
        Assert.AreEqual(0xABCDUL, cursor.JournalId);
        Assert.AreEqual(5000L, cursor.NextUsn);
    }

    [TestMethod]
    public void QueryUsnJournal_JournalNotActive_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.QueryUsnJournal();
        });
        Assert.IsTrue(exception.Message.Contains("not active"));
    }

    [TestMethod]
    public void QueryUsnJournal_DeleteInProgress_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.QueryUsnJournal();
        });
        Assert.IsTrue(exception.Message.Contains("deletion"));
    }

    [TestMethod]
    public void QueryUsnJournal_GenericError_ReturnsErrorCode()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(5, 1); // ERROR_ACCESS_DENIED
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.QueryUsnJournal();
        });
        Assert.IsTrue(exception.Message.Contains("Error: 5"));
    }

    // --- ReadUsnJournal success + parse ---

    [TestMethod]
    public void ReadUsnJournal_SyntheticSuccess_ParsesEntries()
    {
        UseFakeHandle();
        QueueSuccess(BuildReadBuffer(2000,
            (100, 5, 1000, 0x00000100u, "newfile.txt"),
            (200, 5, 1500, 0x00000200u, "deleted.txt")));
        QueueSuccess(BuildReadBuffer(2000)); // same nextUsn, zero records → break

        using var volume = MftVolume.Open("C");
        var (entries, cursor) = volume.ReadUsnJournal(Cursor);

        Assert.AreEqual(2, entries.Length);
        Assert.AreEqual(100UL, entries[0].RecordNumber);
        Assert.AreEqual("newfile.txt", entries[0].FileName);
        Assert.AreEqual(200UL, entries[1].RecordNumber);
        Assert.AreEqual("deleted.txt", entries[1].FileName);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    [TestMethod]
    public void ReadUsnJournal_MultiBuffer_HitsGrowPath()
    {
        UseFakeHandle();
        // >1024 records total across buffers (each IOCTL ≤64KB) triggers the
        // entry-array grow path. Two buffers of 700 short-named records = 1400.
        QueueSuccess(BuildManyRecords(startUsn: 1000, nextUsn: 2000, count: 700));
        QueueSuccess(BuildManyRecords(startUsn: 2000, nextUsn: 3000, count: 700));
        QueueSuccess(BuildReadBuffer(3000)); // terminate

        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.ReadUsnJournal(Cursor);
        Assert.AreEqual(1400, entries.Length);
    }

    static byte[] BuildManyRecords(long startUsn, long nextUsn, int count)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(nextUsn);
        for (var i = 0; i < count; i++)
            WriteRecord(w, (ulong)(i + 1), 5, startUsn + i, 0x00000100u, "f");
        return ms.ToArray();
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnReadBuffer_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetAllocFailCountdown(1); // fail read buffer alloc (before any IOCTL)
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("allocate"));
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnEntryArray_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetAllocFailCountdown(2); // skip read buffer, fail entry array
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("allocate"));
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnGrow_ReturnsError()
    {
        UseFakeHandle();
        // Provide >1024 records so the grow path is reached, then fail the grow alloc.
        QueueSuccess(BuildManyRecords(startUsn: 1000, nextUsn: 2000, count: 700));
        QueueSuccess(BuildManyRecords(startUsn: 2000, nextUsn: 3000, count: 700));
        MFTLibNative.NativeSetAllocFailCountdown(3); // readBuffer, entryArray, then grow → fail
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("grow"));
    }

    [TestMethod]
    public void ReadUsnJournal_JournalNotActive_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("not active"));
    }

    [TestMethod]
    public void ReadUsnJournal_DeleteInProgress_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("deletion"));
    }

    [TestMethod]
    public void ReadUsnJournal_EntryDeleted_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_ENTRY_DELETED, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("rescan"));
    }

    [TestMethod]
    public void ReadUsnJournal_GenericError_ReturnsErrorCode()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(5, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.ReadUsnJournal(Cursor);
        });
        Assert.IsTrue(exception.Message.Contains("Error: 5"));
    }

    [TestMethod]
    public void ReadUsnJournal_HandleEof_ReturnsEmpty()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 1);
        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.ReadUsnJournal(Cursor);
        Assert.AreEqual(0, entries.Length);
    }

    [TestMethod]
    public void ReadUsnJournal_FailCountdownNotYetFired_PassesThroughFirstCall()
    {
        // countdown=2 exercises the ShouldFailUsnIo "armed but not firing yet"
        // branch: first IOCTL passes through (decrement 2→1, returns false), then
        // the second fires EOF to break the read loop cleanly.
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 2);
        QueueSuccess(BuildReadBuffer(2000, (100, 5, 1000, 0x00000100u, "passed.txt")));
        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.ReadUsnJournal(Cursor);
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("passed.txt", entries[0].FileName);
    }

    // --- WatchUsnJournalBatch (direct native calls; the success path uses the
    // overlapped ERROR_IO_PENDING → GetOverlappedResult branch via injection) ---

    [TestMethod]
    public void WatchUsnJournal_SyntheticSuccess_ParsesEntries()
    {
        UseFakeHandle();
        QueueSuccess(BuildReadBuffer(2500, (300, 5, 2400, 0x00000100u, "watched.txt")));
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.AreEqual(1UL, result.EntryCount);
        Assert.AreEqual(2500L, result.NextUsn);
    }

    [TestMethod]
    public void WatchUsnJournal_OverlappedPending_CallsGetOverlappedResult()
    {
        // IO_PENDING WITHOUT abort → the watch takes the real GetOverlappedResult
        // path (on the fake handle it then fails, yielding a generic error). Covers
        // the non-abort branch of UsnGetOverlappedResult.
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(997 /*ERROR_IO_PENDING*/, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        Assert.AreNotEqual(IntPtr.Zero, resultPtr);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
    }

    [TestMethod]
    public void QueryUsnJournal_NoInjection_FallsThroughToRealDeviceIoControl()
    {
        // With neither a fail-hook nor a queued success buffer, UsnDeviceIoControl
        // falls through to the real DeviceIoControl on the fake handle — which fails
        // with ERROR_INVALID_HANDLE. Covers the real-syscall fall-through line and
        // the generic-error branch without admin or a real device.
        UseFakeHandle();
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.QueryUsnJournal();
        });
        Assert.IsTrue(exception.Message.Contains("Error:"), $"got: {exception.Message}");
    }

    [TestMethod]
    public void CancelUsnJournalWatch_OnHandle_Returns()
    {
        // Covers CancelUsnJournalWatch (CancelIoEx). On a handle with no pending
        // I/O it returns false (ERROR_NOT_FOUND) — we only need the call covered.
        MFTLibNative.CancelUsnJournalWatch(FakeHandle());
    }

    [TestMethod]
    public void WatchUsnJournal_OverlappedAbort_ReturnsEmpty()
    {
        UseFakeHandle();
        // Force the IOCTL to report ERROR_IO_PENDING, then the overlapped wait to
        // report ERROR_OPERATION_ABORTED — the watch cancel path.
        MFTLibNative.NativeSetUsnIoFailError(997 /*ERROR_IO_PENDING*/, 1);
        MFTLibNative.NativeSetUsnOverlappedAbort();
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.IsTrue(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnReadBuffer_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetAllocFailCountdown(1); // fail read buffer alloc
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("allocate"));
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnEvent_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetAllocFailCountdown(2); // skip read buffer, fail CreateEvent
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("event") || result.ErrorMessage.Contains("Error"));
    }

    [TestMethod]
    public void WatchUsnJournal_JournalNotActive_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("not active"));
    }

    [TestMethod]
    public void WatchUsnJournal_DeleteInProgress_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("deletion"));
    }

    [TestMethod]
    public void WatchUsnJournal_EntryDeleted_ReturnsError()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_JOURNAL_ENTRY_DELETED, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("rescan"));
    }

    [TestMethod]
    public void WatchUsnJournal_HandleEof_ReturnsEmpty()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.IsTrue(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [TestMethod]
    public void WatchUsnJournal_GenericError_ReturnsErrorCode()
    {
        UseFakeHandle();
        MFTLibNative.NativeSetUsnIoFailError(5, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr = MFTLibNative.WatchUsnJournalBatch(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalId);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.FreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("Error: 5"));
    }
}
