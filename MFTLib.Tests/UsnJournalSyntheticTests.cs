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
    // ReSharper disable InconsistentNaming -- Win32 error code naming convention
    const uint ERROR_HANDLE_EOF = 38;
    const uint ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    const uint ERROR_JOURNAL_NOT_ACTIVE = 1179;
    const uint ERROR_JOURNAL_ENTRY_DELETED = 1181;
    // ReSharper restore InconsistentNaming

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
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
    }

    static void UseDiskSpace(ulong totalBytes, ulong callerTotalAllocationUnits = 1,
        ulong actualAvailableAllocationUnits = 1, ulong callerAvailableAllocationUnits = 1)
    {
        Kernel32.Hooks._getDiskSpaceInformation = (rootPath, out diskSpaceInformation) =>
        {
            Assert.AreEqual(@"C:\", rootPath);
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = totalBytes,
                ActualAvailableAllocationUnits = actualAvailableAllocationUnits,
                CallerTotalAllocationUnits = callerTotalAllocationUnits,
                CallerAvailableAllocationUnits = callerAvailableAllocationUnits,
                SectorsPerAllocationUnit = 1,
                BytesPerSector = 1
            };
            return 0;
        };
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
        using var volume = MftVolume.Open('C');
        var cursor = volume.Journal.QueryUsnJournalCursor();
        Assert.AreEqual(0xABCDUL, cursor.JournalIdentifier);
        Assert.AreEqual(5000L, cursor.NextUsn);
    }

    [TestMethod]
    [DataRow((int)ERROR_JOURNAL_NOT_ACTIVE)]
    [DataRow((int)ERROR_JOURNAL_DELETE_IN_PROGRESS)]
    [DataRow(5)] // ERROR_ACCESS_DENIED
    public void QueryUsnJournal_IoctlFails_ThrowsTheWin32Error(int win32Error)
    {
        UseFakeHandle();
        JournalQueryFake.Fail(win32Error);
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<Win32Exception>(() =>
            journal.QueryUsnJournalCursor());
        Assert.AreEqual(win32Error, exception.NativeErrorCode);
    }

    [TestMethod]
    public void QueryUsnJournalSettings_SyntheticSuccess_ReturnsSizing()
    {
        UseFakeHandle();
        JournalQueryFake.Serve(maximumSize: 0x08000000, allocationDelta: 0x01000000);
        using var volume = MftVolume.Open('C');
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

        using var volume = MftVolume.Open('C');
        var (entries, cursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);

        Assert.HasCount(2, entries);
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

        using var volume = MftVolume.Open('C');
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.HasCount(1400, entries);
    }

    [TestMethod]
    public void ReadUsnJournal_MaximumBufferReadsOne_StopsAfterOneRead()
    {
        UseFakeHandle();
        QueueSuccess(UsnReadBuffers.Build(2000, (100, 5, 1000, 0x00000100u, "first.txt")));
        QueueSuccess(UsnReadBuffers.Build(3000, (200, 5, 2000, 0x00000100u, "second.txt")));
        QueueSuccess(UsnReadBuffers.Build(4000, (300, 5, 3000, 0x00000100u, "third.txt")));

        using var volume = MftVolume.Open('C');
        var (firstEntries, firstCursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 1);

        Assert.HasCount(1, firstEntries);
        Assert.AreEqual("first.txt", firstEntries[0].FileName);
        Assert.AreEqual(2000L, firstCursor.NextUsn);

        var (secondEntries, secondCursor) = volume.Journal.ReadUsnJournalBounded(firstCursor, 1);

        Assert.HasCount(1, secondEntries);
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

        using var volume = MftVolume.Open('C');
        var (entries, cursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);

        Assert.HasCount(3, entries);
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
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("allocate", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnEntryArray_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetAllocFailCountdown(2); // skip read buffer, fail entry array
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("allocate", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_AllocFailOnGrow_ReturnsError()
    {
        UseFakeHandle();
        // Provide >1024 records so the grow path is reached, then fail the grow alloc.
        QueueSuccess(BuildManyRecords(1000, 2000, 700));
        QueueSuccess(BuildManyRecords(2000, 3000, 700));
        NativeTestHooks.NativeSetAllocFailCountdown(3); // readBuffer, entryArray, then grow → fail
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("grow", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_JournalNotActive_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("not active", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_DeleteInProgress_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("deletion", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_EntryDeleted_ReturnsError()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_ENTRY_DELETED, 1);
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("rescan", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_GenericError_ReturnsErrorCode()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(5, 1);
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.ReadUsnJournalBounded(Cursor, 0));
        Assert.Contains("Error: 5", exception.Message);
    }

    [TestMethod]
    public void ReadUsnJournal_ShortSuccessBuffer_ReturnsEmpty()
    {
        UseFakeHandle();
        QueueSuccess(new byte[4]);
        using var volume = MftVolume.Open('C');
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.IsEmpty(entries);
    }

    [TestMethod]
    public void ReadUsnJournal_ZeroLengthRecord_StopsBatch()
    {
        UseFakeHandle();
        QueueSuccess(BuildZeroLengthRecordBuffer(2000));
        QueueSuccess(UsnReadBuffers.Build(2000));
        using var volume = MftVolume.Open('C');
        var (entries, cursor) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.IsEmpty(entries);
        Assert.AreEqual(2000L, cursor.NextUsn);
    }

    [TestMethod]
    public void ReadUsnJournal_HandleEof_ReturnsEmpty()
    {
        UseFakeHandle();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 1);
        using var volume = MftVolume.Open('C');
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.IsEmpty(entries);
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
        using var volume = MftVolume.Open('C');
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        Assert.HasCount(1, entries);
        Assert.AreEqual("passed.txt", entries[0].FileName);
    }

    // --- WatchUsnJournalBatch (direct native calls; the success path uses the
    // overlapped ERROR_IO_PENDING → GetOverlappedResult branch via injection) ---

    [TestMethod]
    public void WatchUsnJournal_SyntheticSuccess_ParsesEntries()
    {
        QueueSuccess(UsnReadBuffers.Build(2500, (300, 5, 2400, 0x00000100u, "watched.txt")));
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
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
        NativeTestHooks.NativeSetUsnIoFailError(997 /*ERROR_IO_PENDING*/, 1);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        Assert.AreNotEqual(IntPtr.Zero, resultPtr);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void QueryUsnJournal_NoInjection_FallsThroughToRealDeviceIoControl()
    {
        // With no scripted reply the query reaches the real DeviceIoControl on the fake handle,
        // which fails with ERROR_INVALID_HANDLE. Covers the real-syscall path without admin.
        const int errorInvalidHandle = 6;
        UseFakeHandle();
        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<Win32Exception>(() =>
            journal.QueryUsnJournalCursor());
        Assert.AreEqual(errorInvalidHandle, exception.NativeErrorCode);
    }

    [TestMethod]
    public void CancelUsnJournalWatch_OnHandle_Returns()
    {
        // Covers CancelUsnJournalWatch (CancelIoEx). On a handle with no pending
        // I/O it returns false (ERROR_NOT_FOUND) - we only need the call covered.
        MFTLibNative.NativeCancelUsnJournalWatch(FakeVolumeHandle.Create());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WatchUsnJournal_OperationAborted_ReturnsEmptyAtOriginalCursor(bool pending)
    {
        NativeTestHooks.NativeSetUsnIoFailError(pending ? 997U : 995U, 1);
        if (pending)
        {
            NativeTestHooks.NativeSetUsnOverlappedAbort();
        }

        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPointer = UncancelableUsnWatch.Read(
            volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
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
        QueueSuccess(BuildZeroLengthRecordBuffer(2500));
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPointer = UncancelableUsnWatch.Read(
            volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
        MFTLibNative.NativeFreeUsnJournalResult(resultPointer);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.AreEqual(2500L, result.NextUsn);
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnReadBuffer_ReturnsError()
    {
        NativeTestHooks.NativeSetAllocFailCountdown(1); // fail read buffer alloc
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.Contains("allocate", result.ErrorMessage);
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnEvent_ReturnsError()
    {
        NativeTestHooks.NativeSetAllocFailCountdown(2); // skip read buffer, fail CreateEvent
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.IsTrue(result.ErrorMessage.Contains("event") || result.ErrorMessage.Contains("Error"));
    }

    [TestMethod]
    public void WatchUsnJournal_AllocFailOnEntryArray_ReportsTheFailureAtTheStartingCursor()
    {
        QueueSuccess(UsnReadBuffers.Build(2500, (300, 5, 2400, 0x00000100u, "watched.txt")));
        NativeTestHooks.NativeSetAllocFailCountdown(3);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPointer = UncancelableUsnWatch.Read(
            volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
        MFTLibNative.NativeFreeUsnJournalResult(resultPointer);
        Assert.AreEqual("Failed to allocate entry array", result.ErrorMessage);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.AreEqual(Cursor.NextUsn, result.NextUsn, "a batch that was not delivered must be read again");
    }

    [TestMethod]
    public void WatchUsnJournal_JournalNotActive_ReturnsError()
    {
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.Contains("not active", result.ErrorMessage);
    }

    [TestMethod]
    public void WatchUsnJournal_DeleteInProgress_ReturnsError()
    {
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_DELETE_IN_PROGRESS, 1);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.Contains("deletion", result.ErrorMessage);
    }

    [TestMethod]
    public void WatchUsnJournal_EntryDeleted_ReturnsError()
    {
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_ENTRY_DELETED, 1);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.Contains("rescan", result.ErrorMessage);
    }

    [TestMethod]
    public void WatchUsnJournal_HandleEof_ReturnsEmpty()
    {
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_HANDLE_EOF, 1);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.AreEqual(0UL, result.EntryCount);
        Assert.IsTrue(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [TestMethod]
    public void WatchUsnJournal_GenericError_ReturnsErrorCode()
    {
        NativeTestHooks.NativeSetUsnIoFailError(5, 1);
        using var volume = FakeVolumeHandle.OpenVolume('C', out var volumeHandle);
        var resultPtr =
            UncancelableUsnWatch.Read(volumeHandle, Cursor.NextUsn, Cursor.JournalIdentifier);
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
        MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
        Assert.Contains("Error: 5", result.ErrorMessage);
    }

    [TestMethod]
    public async Task WatchUsnJournal_NativeError_ThrowsTheMessage()
    {
        UseFakeHandle();
        FileUtilities.Hooks._getWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        NativeTestHooks.NativeSetUsnIoFailError(ERROR_JOURNAL_NOT_ACTIVE, 1);
        using var volume = MftVolume.Open('C');

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in volume.Journal.WatchUsnJournal(Cursor, TestContext.CancellationToken))
            {
            }
        });
        Assert.Contains("not active", exception.Message);
    }

    [TestMethod]
    public async Task WatchUsnJournal_ResultAllocationException_ThrowsNullResult()
    {
        UseFakeHandle();
        FileUtilities.Hooks._getWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        NativeTestHooks.NativeSetAllocFailCountdown(-3);
        using var volume = MftVolume.Open('C');

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in volume.Journal.WatchUsnJournal(Cursor, TestContext.CancellationToken))
            {
            }
        });
        Assert.Contains("WatchUsnJournalBatch returned null", exception.Message);
    }

    [TestMethod]
    public async Task WatchUsnJournal_EmptyBatchThenEntry_YieldsTheEntryWithItsCursor()
    {
        UseFakeHandle();
        FileUtilities.Hooks._getWatchVolumeHandle = _ => FakeVolumeHandle.Create();
        QueueSuccess(UsnReadBuffers.Build(Cursor.NextUsn));
        QueueSuccess(UsnReadBuffers.Build(600, ((0xA9UL << 48) | 0x1246A1, 5, 550, 0x00000100u, "created.txt")));
        using var volume = MftVolume.Open('C');

        await using var enumerator = volume.Journal.WatchUsnJournal(Cursor, TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
        Assert.IsTrue(await enumerator.MoveNextAsync());

        var (entries, cursor) = enumerator.Current;
        Assert.AreEqual("created.txt", entries.Single().FileName);
        Assert.AreEqual(0x1246A1UL, entries[0].RecordNumber);
        Assert.AreEqual((ushort)0xA9, entries[0].SequenceNumber);
        Assert.AreEqual(Cursor.JournalIdentifier, cursor.JournalIdentifier);
        Assert.AreEqual(600L, cursor.NextUsn);
    }

    // --- GrowUsnJournal (managed FSCTL_CREATE_USN_JOURNAL path; Kernel32 seam swapped) ---

    [TestMethod]
    [DataRow(0x200000u)]
    [DataRow(0x100000u)]
    public void GrowUsnJournal_RequestedNotLargerThanCurrent_RefusesWithoutIoctl(uint requestedMaximumSize)
    {
        UseFakeHandle();
        UseDiskSpace(100UL * 1024 * 1024 * 1024);
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

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        InvalidOperationException? caught = null;
        try
        {
            using var volume = MftVolume.Open('C');
            volume.Journal.GrowUsnJournal(requestedMaximumSize, 0x100000);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        Assert.IsNotNull(caught);
        Assert.Contains("only growth", caught.Message);

        Assert.IsFalse(createCalled);
    }

    [TestMethod]
    public void GrowUsnJournal_ValidRequest_IssuesCreateAndReturnsPostChangeSettings()
    {
        UseFakeHandle();
        UseDiskSpace(100UL * 1024 * 1024 * 1024);
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

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        using var volume = MftVolume.Open('C');
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
        UseDiskSpace(100UL * 1024 * 1024 * 1024);
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

        Kernel32.Hooks._deviceIoControl = FakeDeviceIoControl;
        try
        {
            using var volume = MftVolume.Open('C');
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
            using var volume = MftVolume.Open('C');
            volume.Journal.GrowUsnJournal(0, 0x100000);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected
        }

        try
        {
            using var volume = MftVolume.Open('C');
            volume.Journal.GrowUsnJournal(0x400000, 0);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected
        }
    }

    [TestMethod]
    [DataRow(100UL * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024)]
    [DataRow(160UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [DataRow(1024UL * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [DataRow(20_000_039UL, 1_000_001L)]
    public void GrowUsnJournal_AtExactCap_IssuesCreateAndReturnsSettings(ulong volumeBytes, long exactCapBytes)
    {
        UseFakeHandle();
        UseDiskSpace(volumeBytes);
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x1000, allocationDelta: 0x1000)
            : JournalQueryFake.Describe(7, 200, maximumSize: (ulong)exactCapBytes, allocationDelta: 0x1000));
        long capturedMaximum = 0;
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Assert.AreEqual(0x000900E7u, ioControlCode);
            capturedMaximum = Marshal.ReadInt64(inBuffer, 0);
            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var settings = volume.Journal.GrowUsnJournal(exactCapBytes, 0x1000);

        Assert.AreEqual(exactCapBytes, capturedMaximum);
        Assert.AreEqual(exactCapBytes, settings.MaximumSize);
    }

    [TestMethod]
    [DataRow(100UL * 1024 * 1024 * 1024, (5L * 1024 * 1024 * 1024) + 1, 5L * 1024 * 1024 * 1024)]
    [DataRow(160UL * 1024 * 1024 * 1024, (8L * 1024 * 1024 * 1024) + 1, 8L * 1024 * 1024 * 1024)]
    [DataRow(1024UL * 1024 * 1024 * 1024, (8L * 1024 * 1024 * 1024) + 1, 8L * 1024 * 1024 * 1024)]
    [DataRow(20_000_039UL, 1_000_002L, 1_000_001L)]
    [DataRow(1024UL * 1024 * 1024 * 1024, long.MaxValue, 8L * 1024 * 1024 * 1024)]
    public void GrowUsnJournal_AboveCap_RefusesBeforeAnyIoctl(ulong volumeBytes, long requestedMaximumSize,
        long expectedAllowedBytes)
    {
        UseFakeHandle();
        UseDiskSpace(volumeBytes);
        var ioctlCalls = 0;
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            journal.GrowUsnJournal(requestedMaximumSize, 0x1000));

        Assert.AreEqual(0, ioctlCalls);
        Assert.Contains(requestedMaximumSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Message);
        Assert.Contains(expectedAllowedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Message);
    }

    [TestMethod]
    public void GrowUsnJournal_QueriesVolumeSizeAtRequestTimeWithoutCaching()
    {
        UseFakeHandle();
        var volumeSizes = new Queue<ulong>([40UL * 1024 * 1024 * 1024, 100UL * 1024 * 1024 * 1024]);
        var diskSpaceQueries = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (rootPath, out diskSpaceInformation) =>
        {
            Assert.AreEqual(@"C:\", rootPath);
            Interlocked.Increment(ref diskSpaceQueries);
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = volumeSizes.Dequeue(),
                SectorsPerAllocationUnit = 1,
                BytesPerSector = 1
            };
            return 0;
        };
        var ioctlCalls = 0;
        const long requestedMaximum = 3L * 1024 * 1024 * 1024;
        var query = JournalQueryFake.Create(() => ++ioctlCalls == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000)
            : JournalQueryFake.Describe(7, 200, maximumSize: requestedMaximum, allocationDelta: 0x100000));
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            journal.GrowUsnJournal(requestedMaximum, 0x100000));
        Assert.AreEqual(1, diskSpaceQueries);
        Assert.AreEqual(0, ioctlCalls);

        var settings = journal.GrowUsnJournal(requestedMaximum, 0x100000);

        Assert.AreEqual(2, diskSpaceQueries);
        Assert.AreEqual(requestedMaximum, settings.MaximumSize);
    }

    [TestMethod]
    public void GrowUsnJournal_IgnoresCallerQuotaAndAvailableSpaceFields()
    {
        UseFakeHandle();
        // 100 GiB total volume (5 GiB cap), but caller quota and available space are only 1 KiB.
        UseDiskSpace(100UL * 1024 * 1024 * 1024,
            callerTotalAllocationUnits: 1024,
            actualAvailableAllocationUnits: 1024,
            callerAvailableAllocationUnits: 1024);
        const long fiveGiB = 5L * 1024 * 1024 * 1024;
        var queryCount = 0;
        var query = JournalQueryFake.Create(() => ++queryCount == 1
            ? JournalQueryFake.Describe(7, 200, maximumSize: 0x200000, allocationDelta: 0x100000)
            : JournalQueryFake.Describe(7, 200, maximumSize: fiveGiB, allocationDelta: 0x100000));
        Kernel32.Hooks._deviceIoControl = (device, ioControlCode, inBuffer,
            inBufferSize, outBuffer, outBufferSize, out bytesReturned, overlapped) =>
        {
            if (ioControlCode == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, ioControlCode, inBuffer, inBufferSize,
                    outBuffer, outBufferSize, out bytesReturned, overlapped);
            }

            bytesReturned = 0;
            return true;
        };

        using var volume = MftVolume.Open('C');
        var settings = volume.Journal.GrowUsnJournal(fiveGiB, 0x100000);

        Assert.AreEqual(fiveGiB, settings.MaximumSize);
    }

    [TestMethod]
    public void GrowUsnJournal_DiskSpaceQueryFails_ThrowsWin32ExceptionBeforeAnyIoctl()
    {
        UseFakeHandle();
        const int failedHresult = unchecked((int)0x80070005);
        var ioctlCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            diskSpaceInformation = default;
            return failedHresult;
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        var exception = Assert.ThrowsExactly<Win32Exception>(() =>
            journal.GrowUsnJournal(0x400000, 0x100000));

        Assert.AreEqual(failedHresult, exception.NativeErrorCode);
        Assert.AreEqual(0, ioctlCalls);
    }

    [TestMethod]
    [DataRow(0UL, 8u, 512u)]
    [DataRow(1000UL, 0u, 512u)]
    [DataRow(1000UL, 8u, 0u)]
    [DataRow(19UL, 1u, 1u)]
    [DataRow(ulong.MaxValue, 2u, 512u)]
    public void GrowUsnJournal_UnusableGeometryOrOverflow_ThrowsInvalidOperationBeforeAnyIoctl(
        ulong totalUnits, uint sectorsPerUnit, uint bytesPerSector)
    {
        UseFakeHandle();
        var ioctlCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            diskSpaceInformation = new DiskSpaceInformation
            {
                ActualTotalAllocationUnits = totalUnits,
                SectorsPerAllocationUnit = sectorsPerUnit,
                BytesPerSector = bytesPerSector
            };
            return 0;
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var volume = MftVolume.Open('C');
        var journal = volume.Journal;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.GrowUsnJournal(0x400000, 0x100000));

        Assert.AreEqual(0, ioctlCalls);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("C:")]
    [DataRow(@"\\.\c:")]
    [DataRow(@"\\.\C:\")]
    [DataRow(@"\\.\1:")]
    [DataRow(@"\\.\C!")]
    public void GrowUsnJournal_InvalidVolumePath_ThrowsBeforeQueryingDiskSpaceOrIoctl(string invalidVolumePath)
    {
        var diskSpaceCalls = 0;
        var ioctlCalls = 0;
        Kernel32.Hooks._getDiskSpaceInformation = (_, out diskSpaceInformation) =>
        {
            Interlocked.Increment(ref diskSpaceCalls);
            diskSpaceInformation = default;
            return 0;
        };
        Kernel32.Hooks._deviceIoControl = (_, _, _, _, _, _, out bytesReturned, _) =>
        {
            Interlocked.Increment(ref ioctlCalls);
            bytesReturned = 0;
            return false;
        };

        using var handle = FakeVolumeHandle.Create();
        var journal = new UsnJournalVolume(handle, invalidVolumePath);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.GrowUsnJournal(0x400000, 0x100000));
        Assert.AreEqual(0, diskSpaceCalls);
        Assert.AreEqual(0, ioctlCalls);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void GetDiskSpaceInformation_OnWindowsSystemRoot_ReturnsPositiveGeometry()
    {
        var hresult = Kernel32.Hooks._getDiskSpaceInformation(@"C:\", out var diskSpace);

        Assert.AreEqual(0, hresult);
        Assert.IsGreaterThan(0UL, diskSpace.ActualTotalAllocationUnits);
        Assert.IsGreaterThan(0u, diskSpace.SectorsPerAllocationUnit);
        Assert.IsGreaterThan(0u, diskSpace.BytesPerSector);
    }
}
