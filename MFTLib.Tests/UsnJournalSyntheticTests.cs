using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

/// <summary>
///     Covers usn_journal.cpp WITHOUT admin, by injecting synthetic IOCTL responses
///     and failures through the native test seams (SetUsnIoSuccess / SetUsnIoFailError
///     / SetUsnOverlappedAbort). The native code parses our buffers exactly as it would
///     real kernel output, so the success, record-parse, grow, error, and watch/cancel
///     paths are all exercised with no elevated volume handle.
///     These replace the old RequiresAdmin USN tests that opened the live C: volume.
///     The genuinely-live integration smoke lives in UsnJournalLiveTests (LiveVolume).
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class UsnJournalSyntheticTests
{
    // Win32 error constants (mirror the native error branches).
    const uint ERROR_HANDLE_EOF = 38;
    const uint ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    const uint ERROR_JOURNAL_NOT_ACTIVE = 1179;
    const uint ERROR_JOURNAL_ENTRY_DELETED = 1181;

    static readonly UsnJournalCursor Cursor = new(0xABCD, 500);

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        Kernel32.Hooks.ResetToDefaults();
        JournalQueryFake.Reset();
        QueuedUsnReads.Release();
    }

    static void UseFakeHandle()
    {
        FileUtilities.Hooks.GetVolumeHandle = _ => FakeVolumeHandle.Create();
    }

    // Queues a synthetic IOCTL success buffer; keeps it alive until cleanup.
    static void QueueSuccess(byte[] data) => QueuedUsnReads.Queue(data);

    static byte[] BuildZeroLengthRecordBuffer(long nextUsn)
    {
        var data = new byte[sizeof(long) + 64];
        BitConverter.GetBytes(nextUsn).CopyTo(data, 0);
        return data;
    }

    // --- QueryUsnJournal: the managed query path, scripted through JournalQueryFake ---

    [TestMethod]
    public void QueryUsnJournal_SyntheticSuccess_ReturnsCursor()
    {
        UseFakeHandle();
        JournalQueryFake.Serve(journalIdentifier: 0xABCD, nextUsn: 5000);
        using var volume = MftVolume.Open("C");
        var cursor = volume.Journal.QueryUsnJournalCursor();
        Assert.AreEqual(0xABCDUL, cursor.JournalIdentifier);
        Assert.AreEqual(5000L, cursor.NextUsn);
    }

    [DataTestMethod]
    [DataRow((int)ERROR_JOURNAL_NOT_ACTIVE)]
    [DataRow((int)ERROR_JOURNAL_DELETE_IN_PROGRESS)]
    [DataRow(5)] // ERROR_ACCESS_DENIED
    public void QueryUsnJournal_IoctlFails_ThrowsTheWin32Error(int win32Error)
    {
        UseFakeHandle();
        JournalQueryFake.Fail(win32Error);
        var exception = Assert.ThrowsException<Win32Exception>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.QueryUsnJournalCursor();
        });
        Assert.AreEqual(win32Error, exception.NativeErrorCode);
    }

    [TestMethod]
    public void QueryUsnJournalSettings_SyntheticSuccess_ReturnsSizing()
    {
        UseFakeHandle();
        JournalQueryFake.Serve(maximumSize: 0x08000000, allocationDelta: 0x01000000);
        using var volume = MftVolume.Open("C");
        var settings = volume.Journal.QueryUsnJournalSettings();
        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);
    }

    // --- ReadUsnJournalBounded success + parse ---

    [TestMethod]
    public void ReadUsnJournal_SyntheticSuccess_ParsesEntries()
    {
        UseFakeHandle();
        QueueSuccess(UsnReadBuffers.Build(2000,
            (100, 5, 1000, 0x00000100u, "newfile.txt"),
            (200, 5, 1500, 0x00000200u, "deleted.txt")));
        QueueSuccess(UsnReadBuffers.Build(2000)); // same nextUsn, zero records → break

        using var volume = MftVolume.Open("C");
        var (entries, cursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);

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
        QueueSuccess(BuildManyRecords(1000, 2000, 700));
        QueueSuccess(BuildManyRecords(2000, 3000, 700));
        QueueSuccess(UsnReadBuffers.Build(3000)); // terminate

        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.AreEqual(1400, entries.Length);
    }

    [TestMethod]
    public void ReadUsnJournal_MaximumBufferReadsOne_StopsAfterOneRead()
    {
        UseFakeHandle();
        QueueSuccess(UsnReadBuffers.Build(2000, (100, 5, 1000, 0x00000100u, "first.txt")));
        QueueSuccess(UsnReadBuffers.Build(3000, (200, 5, 2000, 0x00000100u, "second.txt")));
        QueueSuccess(UsnReadBuffers.Build(4000, (300, 5, 3000, 0x00000100u, "third.txt")));

        using var volume = MftVolume.Open("C");
        var (firstEntries, firstCursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 1);

        Assert.AreEqual(1, firstEntries.Length);
        Assert.AreEqual("first.txt", firstEntries[0].FileName);
        Assert.AreEqual(2000L, firstCursor.NextUsn);

        var (secondEntries, secondCursor) = volume.Journal.ReadUsnJournalBounded(firstCursor, 1);

        Assert.AreEqual(1, secondEntries.Length);
        Assert.AreEqual("second.txt", secondEntries[0].FileName);
        Assert.AreEqual(3000L, secondCursor.NextUsn);
    }

    [TestMethod]
    public void ReadUsnJournal_MaximumBufferReadsZero_ReadsToTip()
    {
        UseFakeHandle();
        QueueSuccess(UsnReadBuffers.Build(2000, (100, 5, 1000, 0x00000100u, "first.txt")));
        QueueSuccess(UsnReadBuffers.Build(3000, (200, 5, 2000, 0x00000100u, "second.txt")));
        QueueSuccess(UsnReadBuffers.Build(4000, (300, 5, 3000, 0x00000100u, "third.txt")));
        QueueSuccess(UsnReadBuffers.Build(4000));

        using var volume = MftVolume.Open("C");
        var (entries, cursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);

        Assert.AreEqual(3, entries.Length);
        Assert.AreEqual(4000L, cursor.NextUsn);
    }

    static byte[] BuildManyRecords(long startUsn, long nextUsn, int count)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(nextUsn);
        for (var i = 0; i < count; i++)
        {
            UsnReadBuffers.WriteRecord(w, (ulong)(i + 1), 5, startUsn + i, 0x00000100u, "f");
        }

        return ms.ToArray();
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnReadBuffer_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetAllocFailCountdown(1); // fail read buffer alloc (before any IOCTL)
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("allocate"));
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnEntryArray_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetAllocFailCountdown(2); // skip read buffer, fail entry array
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("allocate"));
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnGrow_ReturnsError()
    {
        UseFakeHandle();
        // Provide >1024 records so the grow path is reached, then fail the grow alloc.
        QueueSuccess(BuildManyRecords(1000, 2000, 700));
        QueueSuccess(BuildManyRecords(2000, 3000, 700));
        NativeTestHooks.NativeSetAllocFailCountdown(3); // readBuffer, entryArray, then grow → fail
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("grow"));
    }

    [TestMethod]
    public void ReadUsnJournal_JournalNotActive_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("not active"));
    }

    [TestMethod]
    public void ReadUsnJournal_DeleteInProgress_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("deletion"));
    }

    [TestMethod]
    public void ReadUsnJournal_EntryDeleted_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_ENTRY_DELETED, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("rescan"));
    }

    [TestMethod]
    public void ReadUsnJournal_GenericError_ReturnsErrorCode()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(5, 1);
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.IsTrue(exception.Message.Contains("Error: 5"));
    }

    [TestMethod]
    public void ReadUsnJournal_ShortSuccessBuffer_ReturnsEmpty()
    {
        UseFakeHandle();
        QueueSuccess(new byte[4]);
        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.AreEqual(0, entries.Length);
    }

    [TestMethod]
    public void ReadUsnJournal_ZeroLengthRecord_StopsBatch()
    {
        UseFakeHandle();
        QueueSuccess(BuildZeroLengthRecordBuffer(2000));
        QueueSuccess(UsnReadBuffers.Build(2000));
        using var volume = MftVolume.Open("C");
        var (entries, cursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.AreEqual(0, entries.Length);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    [TestMethod]
    public void ReadUsnJournal_HandleEof_ReturnsEmpty()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 1);
        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.AreEqual(0, entries.Length);
    }

    [TestMethod]
    public void ReadUsnJournal_FailCountdownNotYetFired_PassesThroughFirstCall()
    {
        // countdown=2 exercises the ShouldFailUsnIo "armed but not firing yet"
        // branch: first IOCTL passes through (decrement 2→1, returns false), then
        // the second fires EOF to break the read loop cleanly.
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 2);
        QueueSuccess(UsnReadBuffers.Build(2000, (100, 5, 1000, 0x00000100u, "passed.txt")));
        using var volume = MftVolume.Open("C");
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("passed.txt", entries[0].FileName);
    }

    // --- WatchUsnJournalBatch (direct native calls; the success path uses the
    // overlapped ERROR_IO_PENDING → GetOverlappedResult branch via injection) ---

    [TestMethod]
    public void WatchUsnJournal_SyntheticSuccess_ParsesEntries()
    {
        UseFakeHandle();
        QueueSuccess(UsnReadBuffers.Build(2500, (300, 5, 2400, 0x00000100u, "watched.txt")));
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
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
        NativeTestHooks.NativeSetUsnIoFailError(997 /*ERROR_IO_PENDING*/, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        Assert.AreNotEqual(IntPtr.Zero, resultPtr);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
    }

    [TestMethod]
    public void QueryUsnJournal_NoInjection_FallsThroughToRealDeviceIoControl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The real query is a kernel32 DeviceIoControl call.");
            return;
        }

        // With no scripted reply the query reaches the real DeviceIoControl on the fake handle,
        // which fails with ERROR_INVALID_HANDLE. Covers the real-syscall path without admin.
        const int errorInvalidHandle = 6;
        UseFakeHandle();
        var exception = Assert.ThrowsException<Win32Exception>(() =>
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.QueryUsnJournalCursor();
        });
        Assert.AreEqual(errorInvalidHandle, exception.NativeErrorCode);
    }

    [TestMethod]
    public void CancelUsnJournalWatch_OnHandle_Returns()
    {
        // Covers CancelUsnJournalWatch (CancelIoEx). On a handle with no pending
        // I/O it returns false (ERROR_NOT_FOUND) — we only need the call covered.
        MFTLibNative.NativeCancelUsnJournalWatch(FakeVolumeHandle.Create());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WatchUsnJournal_OperationAborted_ReturnsEmptyAtOriginalCursor(bool pending)
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(pending ? 997U : 995U, 1);
        if (pending)
        {
            NativeTestHooks.NativeSetUsnOverlappedAbort();
        }

        using var volume = MftVolume.Open("C");
        var resultPointer = UncancelableUsnWatch.Read(
            volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
            Assert.AreEqual(0UL, result.EntryCount);
            Assert.IsTrue(string.IsNullOrEmpty(result.ErrorMessage), result.ErrorMessage);
            Assert.AreEqual(Cursor.NextUsn, result.NextUsn);
            Assert.AreEqual(Cursor.JournalIdentifier, result.JournalIdentifier);
        }
        finally
        {
            MFTLibNative.NativeFreeUsnJournalResult(resultPointer);
        }
    }

    [TestMethod]
    public void WatchUsnJournal_ZeroLengthRecord_ReturnsEmptyAtAdvancedCursor()
    {
        UseFakeHandle();
        QueueSuccess(BuildZeroLengthRecordBuffer(2500));
        using var volume = MftVolume.Open("C");
        var resultPointer = UncancelableUsnWatch.Read(
            volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
        MFTLibNative.NativeFreeUsnJournalResult(resultPointer);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.AreEqual(2500L, result.NextUsn);
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnReadBuffer_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetAllocFailCountdown(1); // fail read buffer alloc
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("allocate"));
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnEvent_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetAllocFailCountdown(2); // skip read buffer, fail CreateEvent
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("event") || result.ErrorMessage.Contains("Error"));
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnEntryArray_ReportsTheFailureAtTheStartingCursor()
    {
        UseFakeHandle();
        QueueSuccess(UsnReadBuffers.Build(2500, (300, 5, 2400, 0x00000100u, "watched.txt")));
        NativeTestHooks.NativeSetAllocFailCountdown(3);
        using var volume = MftVolume.Open("C");
        var resultPointer = UncancelableUsnWatch.Read(
            volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
        MFTLibNative.NativeFreeUsnJournalResult(resultPointer);
        Assert.AreEqual("Failed to allocate entry array", result.ErrorMessage);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.AreEqual(Cursor.NextUsn, result.NextUsn, "a batch that was not delivered must be read again");
    }

    [TestMethod]
    public void WatchUsnJournal_JournalNotActive_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("not active"));
    }

    [TestMethod]
    public void WatchUsnJournal_DeleteInProgress_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("deletion"));
    }

    [TestMethod]
    public void WatchUsnJournal_EntryDeleted_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_ENTRY_DELETED, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("rescan"));
    }

    [TestMethod]
    public void WatchUsnJournal_HandleEof_ReturnsEmpty()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.IsTrue(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [TestMethod]
    public void WatchUsnJournal_GenericError_ReturnsErrorCode()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(5, 1);
        using var volume = MftVolume.Open("C");
        var resultPtr =
            UncancelableUsnWatch.Read(volume.GetVolumeHandleForTest(), Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("Error: 5"));
    }

    [TestMethod]
    public async Task WatchUsnJournal_NativeError_ThrowsTheMessage()
    {
        UseFakeHandle();
        FileUtilities.Hooks.GetWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        using var volume = MftVolume.Open("C");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in volume.Journal.WatchUsnJournal(Cursor))
            {
            }
        });
        StringAssert.Contains(exception.Message, "not active");
    }

    [TestMethod]
    public async Task WatchUsnJournal_ResultAllocationException_ThrowsNullResult()
    {
        UseFakeHandle();
        FileUtilities.Hooks.GetWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        NativeTestHooks.NativeSetAllocFailCountdown(-3);
        using var volume = MftVolume.Open("C");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in volume.Journal.WatchUsnJournal(Cursor))
            {
            }
        });
        StringAssert.Contains(exception.Message, "WatchUsnJournalBatch returned null");
    }

    [TestMethod]
    public async Task WatchUsnJournal_EmptyBatchThenEntry_YieldsTheEntryWithItsCursor()
    {
        UseFakeHandle();
        FileUtilities.Hooks.GetWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        QueueSuccess(UsnReadBuffers.Build(Cursor.NextUsn));
        QueueSuccess(UsnReadBuffers.Build(600, ((0xA9UL << 48) | 0x1246A1, 5, 550, 0x00000100u, "created.txt")));
        using var volume = MftVolume.Open("C");

        await using var enumerator = volume.Journal.WatchUsnJournal(Cursor).GetAsyncEnumerator();
        Assert.IsTrue(await enumerator.MoveNextAsync());

        var (entries, cursor) = enumerator.Current;
        Assert.AreEqual("created.txt", entries.Single().FileName);
        Assert.AreEqual(0x1246A1UL, entries[0].RecordNumber);
        Assert.AreEqual((ushort)0xA9, entries[0].SequenceNumber);
        Assert.AreEqual(Cursor.JournalIdentifier, cursor.JournalIdentifier);
        Assert.AreEqual(600L, cursor.NextUsn);
    }

    // --- GrowUsnJournal (managed FSCTL_CREATE_USN_JOURNAL path; Kernel32 seam swapped) ---

    [DataTestMethod]
    [DataRow(0x200000u)]
    [DataRow(0x100000u)]
    public void GrowUsnJournal_RequestedNotLargerThanCurrent_RefusesWithoutIoctl(uint requestedMaximumSize)
    {
        UseFakeHandle();
        var query = JournalQueryFake.Create(() =>
            JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000));
        var createCalled = false;
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }
            Assert.AreEqual(0x000900E7u, ioControlCode);
            createCalled = true;
            bytesReturned = 0;
            return true;
        }

        Kernel32.Hooks.DeviceIoControl = FakeDeviceIoControl;
        try
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.GrowUsnJournal(requestedMaximumSize, 0x100000);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException exception)
        {
            StringAssert.Contains(exception.Message, "only growth");
        }

        Assert.IsFalse(createCalled);
    }

    [TestMethod]
    public void GrowUsnJournal_ValidRequest_IssuesCreateAndReturnsPostChangeSettings()
    {
        UseFakeHandle();
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000) // pre-check
            : JournalQueryFake.Describe(7, 200, maximumSize: 0x400000, allocationDelta: 0x200000)); // post-change
        uint? capturedCode = null;
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
            capturedCode = ioControlCode;
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            capturedDelta = Marshal.ReadInt64(inBuffer, 8);
            bytesReturned = 0;
            return true;
        }

        Kernel32.Hooks.DeviceIoControl = FakeDeviceIoControl;
        using var volume = MftVolume.Open("C");
        var settings = volume.Journal.GrowUsnJournal(0x400000, 0x200000);
        Assert.AreEqual(0x000900E7u, capturedCode);
        Assert.AreEqual(0x400000L, capturedMaximum);
        Assert.AreEqual(0x200000L, capturedDelta);
        Assert.AreEqual(0x400000L, settings.MaximumSize);
        Assert.AreEqual(0x200000L, settings.AllocationDelta);
    }

    [TestMethod]
    public void GrowUsnJournal_CreateFails_ThrowsWin32Exception()
    {
        UseFakeHandle();
        var query = JournalQueryFake.Create(() =>
            JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000));
        bool FakeDeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
            uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }
            Assert.AreEqual(0x000900E7u, ioControlCode);
            bytesReturned = 0;
            return false;
        }

        Kernel32.Hooks.DeviceIoControl = FakeDeviceIoControl;
        try
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.GrowUsnJournal(0x400000, 0x100000);
            Assert.Fail("Expected Win32Exception");
        }
        catch (Win32Exception)
        {
            // Expected
        }
    }

    [TestMethod]
    public void GrowUsnJournal_NonPositiveSizes_ThrowBeforeAnyIoctl()
    {
        UseFakeHandle();
        try
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.GrowUsnJournal(0, 0x100000);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected
        }

        try
        {
            using var volume = MftVolume.Open("C");
            volume.Journal.GrowUsnJournal(0x400000, 0);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected
        }
    }
}
