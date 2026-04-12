using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using MFTLib.Interop;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    static SafeFileHandle FakeHandle() => new(new IntPtr(1), ownsHandle: false);

    [TestMethod]
    public void QueryUsnJournal_ReturnsValidCursor()
    {
        var info = new UsnJournalInfoNative
        {
            JournalId = 0x123456789ABCDEF0,
            FirstUsn = 0,
            NextUsn = 42000,
            LowestValidUsn = 100,
            MaxUsn = long.MaxValue,
            MaximumSize = 32 * 1024 * 1024,
            AllocationDelta = 4 * 1024 * 1024,
        };

        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(info, infoPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.QueryUsnJournal = _ => infoPtr;
        MFTLibNative.FreeUsnJournalInfo = _ => Marshal.FreeHGlobal(infoPtr);

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        Assert.AreEqual(0x123456789ABCDEF0UL, cursor.JournalId);
        Assert.AreEqual(42000L, cursor.NextUsn);
    }

    [TestMethod]
    public void QueryUsnJournal_WithError_Throws()
    {
        var info = new UsnJournalInfoNative
        {
            ErrorMessage = "FSCTL_QUERY_USN_JOURNAL failed: error 5",
        };

        var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
        Marshal.StructureToPtr(info, infoPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.QueryUsnJournal = _ => infoPtr;
        MFTLibNative.FreeUsnJournalInfo = _ => Marshal.FreeHGlobal(infoPtr);

        using var volume = MftVolume.Open("C");
        var exception = Assert.ThrowsException<InvalidOperationException>(() => volume.QueryUsnJournal());
        Assert.IsTrue(exception.Message.Contains("error 5"));
    }

    [TestMethod]
    public void QueryUsnJournal_NullPointer_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.QueryUsnJournal = _ => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        Assert.ThrowsException<InvalidOperationException>(() => volume.QueryUsnJournal());
    }

    [TestMethod]
    public unsafe void ReadUsnJournal_ReturnsEntries()
    {
        const int entrySize = MftVolume.NativeUsnEntrySize;
        var entryCount = 2;
        var entriesSize = entrySize * entryCount;
        var entriesPtr = Marshal.AllocHGlobal(entriesSize);
        new Span<byte>((void*)entriesPtr, entriesSize).Clear();

        // Entry 0: file create
        var ptr = (byte*)entriesPtr;
        *(ulong*)ptr = 100;                 // recordNumber
        *(ulong*)(ptr + 8) = 5;            // parentRecordNumber
        *(long*)(ptr + 16) = 1000;         // usn
        *(long*)(ptr + 24) = 0;            // timestamp (0 = DateTime.MinValue)
        *(uint*)(ptr + 32) = 0x00000100;   // reason = FileCreate
        *(uint*)(ptr + 36) = 0x20;         // fileAttributes = Archive
        var name0 = "newfile.txt";
        *(ushort*)(ptr + 40) = (ushort)name0.Length;
        name0.AsSpan().CopyTo(new Span<char>(ptr + 42, name0.Length));

        // Entry 1: file delete
        ptr = (byte*)entriesPtr + entrySize;
        *(ulong*)ptr = 200;
        *(ulong*)(ptr + 8) = 5;
        *(long*)(ptr + 16) = 2000;
        *(long*)(ptr + 24) = 0;
        *(uint*)(ptr + 32) = 0x00000200;   // reason = FileDelete
        *(uint*)(ptr + 36) = 0;
        var name1 = "deleted.txt";
        *(ushort*)(ptr + 40) = (ushort)name1.Length;
        name1.AsSpan().CopyTo(new Span<char>(ptr + 42, name1.Length));

        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 2,
            Entries = entriesPtr,
            NextUsn = 3000,
            JournalId = 0xABCD,
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ReadUsnJournal = (_, _, _) => resultPtr;
        MFTLibNative.FreeUsnJournalResult = _ =>
        {
            Marshal.FreeHGlobal(entriesPtr);
            Marshal.FreeHGlobal(resultPtr);
        };

        using var volume = MftVolume.Open("C");
        var cursor = new UsnJournalCursor(0xABCD, 500);
        var (entries, updatedCursor) = volume.ReadUsnJournal(cursor);

        Assert.AreEqual(2, entries.Length);
        Assert.AreEqual(100UL, entries[0].RecordNumber);
        Assert.AreEqual("newfile.txt", entries[0].FileName);
        Assert.IsTrue(entries[0].IsCreate);
        Assert.AreEqual(200UL, entries[1].RecordNumber);
        Assert.AreEqual("deleted.txt", entries[1].FileName);
        Assert.IsTrue(entries[1].IsDelete);
        Assert.AreEqual(3000L, updatedCursor.NextUsn);
        Assert.AreEqual(0xABCDUL, updatedCursor.JournalId);
    }

    [TestMethod]
    public void ReadUsnJournal_EmptyJournal_ReturnsEmptyArray()
    {
        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 0,
            Entries = IntPtr.Zero,
            NextUsn = 500,
            JournalId = 0xABCD,
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ReadUsnJournal = (_, _, _) => resultPtr;
        MFTLibNative.FreeUsnJournalResult = _ => Marshal.FreeHGlobal(resultPtr);

        using var volume = MftVolume.Open("C");
        var (entries, updatedCursor) = volume.ReadUsnJournal(new UsnJournalCursor(0xABCD, 500));

        Assert.AreEqual(0, entries.Length);
        Assert.AreEqual(500L, updatedCursor.NextUsn);
    }

    [TestMethod]
    public void ReadUsnJournal_WithError_Throws()
    {
        var nativeResult = new UsnJournalResultNative
        {
            ErrorMessage = "Requested USN has been overwritten",
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPtr, false);

        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ReadUsnJournal = (_, _, _) => resultPtr;
        MFTLibNative.FreeUsnJournalResult = _ => Marshal.FreeHGlobal(resultPtr);

        using var volume = MftVolume.Open("C");
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => volume.ReadUsnJournal(new UsnJournalCursor(0xABCD, 500)));
        Assert.IsTrue(exception.Message.Contains("overwritten"));
    }

    [TestMethod]
    public void ReadUsnJournal_NullPointer_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        MFTLibNative.ReadUsnJournal = (_, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        Assert.ThrowsException<InvalidOperationException>(
            () => volume.ReadUsnJournal(new UsnJournalCursor(0xABCD, 500)));
    }

    [TestMethod]
    public void UsnJournalEntry_ReasonHelpers_Work()
    {
        var create = new UsnJournalEntry(1, 5, 100, 0, (uint)UsnReason.FileCreate, 0, "test.txt");
        Assert.IsTrue(create.IsCreate);
        Assert.IsFalse(create.IsDelete);
        Assert.IsFalse(create.IsClose);
        Assert.IsFalse(create.IsRename);

        var delete = new UsnJournalEntry(1, 5, 200, 0, (uint)(UsnReason.FileDelete | UsnReason.Close), 0, "gone.txt");
        Assert.IsTrue(delete.IsDelete);
        Assert.IsTrue(delete.IsClose);

        var rename = new UsnJournalEntry(1, 5, 300, 0, (uint)UsnReason.RenameNewName, 0, "renamed.txt");
        Assert.IsTrue(rename.IsRename);
    }

    [TestMethod]
    public void UsnJournalEntry_ToString_IncludesReasonAndName()
    {
        var entry = new UsnJournalEntry(42, 5, 100, 0, (uint)UsnReason.FileCreate, 0, "test.txt");
        var text = entry.ToString();
        Assert.IsTrue(text.Contains("FileCreate"));
        Assert.IsTrue(text.Contains("test.txt"));
        Assert.IsTrue(text.Contains("42"));
    }

    [TestMethod]
    public void QueryUsnJournal_Disposed_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        var volume = MftVolume.Open("C");
        volume.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => volume.QueryUsnJournal());
    }

    [TestMethod]
    public void ReadUsnJournal_Disposed_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        var volume = MftVolume.Open("C");
        volume.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => volume.ReadUsnJournal(new UsnJournalCursor(1, 0)));
    }

    [TestMethod]
    public async Task WatchUsnJournal_Disposed_Throws()
    {
        FileUtilities.GetVolumeHandle = _ => FakeHandle();
        var volume = MftVolume.Open("C");
        volume.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in volume.WatchUsnJournal(new UsnJournalCursor(1, 0)))
            {
            }
        });
    }

    [TestMethod]
    public void UsnJournalEntry_PositiveTimestamp_ParsesAsDateTime()
    {
        // 2020-01-01 00:00:00 UTC as FILETIME
        var filetime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var entry = new UsnJournalEntry(1, 5, 100, filetime, (uint)UsnReason.FileCreate, 0x20, "timestamped.txt");

        Assert.AreEqual(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), entry.Timestamp);
        Assert.AreEqual(100L, entry.Usn);
        Assert.AreEqual(5UL, entry.ParentRecordNumber);
        Assert.AreEqual(FileAttributes.Archive, entry.FileAttributes);
    }

    [TestMethod]
    public void UsnJournalEntry_ZeroTimestamp_ReturnsMinValue()
    {
        var entry = new UsnJournalEntry(1, 5, 100, 0, (uint)UsnReason.FileCreate, 0, "test.txt");
        Assert.AreEqual(DateTime.MinValue, entry.Timestamp);
    }

    [TestMethod]
    public void UsnJournalCursor_StoresValues()
    {
        var cursor = new UsnJournalCursor(0xDEADBEEF, 12345);
        Assert.AreEqual(0xDEADBEEFUL, cursor.JournalId);
        Assert.AreEqual(12345L, cursor.NextUsn);
    }

    [TestMethod]
    public async Task WatchUsnJournal_YieldsBatchThenCancels()
    {
        var callCount = 0;

        MFTLibNative.WatchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return BuildSingleEntryWatchResult(journalId, startUsn + 100, "created.txt", 0x00000100);
            }
            return BuildEmptyWatchResult(journalId, startUsn);
        };
        MFTLibNative.CancelUsnJournalWatch = _ => true;
        MFTLibNative.FreeUsnJournalResult = ptr => Marshal.FreeHGlobal(ptr);
        FileUtilities.GetVolumeHandle = _ => FakeHandle();

        using var volume = MftVolume.Open("C");
        using var cancellationTokenSource = new CancellationTokenSource();
        var batches = new List<UsnJournalEntry[]>();

        await foreach (var batch in volume.WatchUsnJournal(new UsnJournalCursor(0xABCD, 500), cancellationTokenSource.Token))
        {
            batches.Add(batch);
            cancellationTokenSource.Cancel();
        }

        Assert.AreEqual(1, batches.Count);
        Assert.AreEqual("created.txt", batches[0][0].FileName);
        Assert.IsTrue(batches[0][0].IsCreate);
    }

    [TestMethod]
    public async Task WatchUsnJournal_ErrorInBatch_Throws()
    {
        MFTLibNative.WatchUsnJournalBatch = (_, _, _) =>
        {
            var nativeResult = new UsnJournalResultNative
            {
                ErrorMessage = "USN journal is not active",
            };
            var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
            Marshal.StructureToPtr(nativeResult, resultPtr, false);
            return resultPtr;
        };
        MFTLibNative.CancelUsnJournalWatch = _ => true;
        MFTLibNative.FreeUsnJournalResult = ptr => Marshal.FreeHGlobal(ptr);
        FileUtilities.GetVolumeHandle = _ => FakeHandle();

        using var volume = MftVolume.Open("C");
        using var cancellationTokenSource = new CancellationTokenSource();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in volume.WatchUsnJournal(new UsnJournalCursor(0xABCD, 500), cancellationTokenSource.Token))
            {
            }
        });
    }

    [TestMethod]
    public async Task WatchUsnJournal_NullPointer_Throws()
    {
        MFTLibNative.WatchUsnJournalBatch = (_, _, _) => IntPtr.Zero;
        MFTLibNative.CancelUsnJournalWatch = _ => true;
        FileUtilities.GetVolumeHandle = _ => FakeHandle();

        using var volume = MftVolume.Open("C");
        using var cancellationTokenSource = new CancellationTokenSource();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in volume.WatchUsnJournal(new UsnJournalCursor(0xABCD, 500), cancellationTokenSource.Token))
            {
            }
        });
    }

    [TestMethod]
    public async Task WatchUsnJournal_EmptyBatchWithCancellation_YieldsBreak()
    {
        // Simulate the race: native call returns empty (CancelIoEx fired mid-call)
        // and the token is already cancelled by the time we check.
        using var cancellationTokenSource = new CancellationTokenSource();

        MFTLibNative.WatchUsnJournalBatch = (_, startUsn, journalId) =>
        {
            // Simulate CancelIoEx race: cancel token then return empty result
            cancellationTokenSource.Cancel();
            return BuildEmptyWatchResult(journalId, startUsn);
        };
        MFTLibNative.CancelUsnJournalWatch = _ => true;
        MFTLibNative.FreeUsnJournalResult = _ => { };
        FileUtilities.GetVolumeHandle = _ => FakeHandle();

        using var volume = MftVolume.Open("C");
        var batches = new List<UsnJournalEntry[]>();

        await foreach (var batch in volume.WatchUsnJournal(new UsnJournalCursor(0xABCD, 500), cancellationTokenSource.Token))
        {
            batches.Add(batch);
        }

        Assert.AreEqual(0, batches.Count);
    }

    // --- Watch test helpers ---

    static unsafe IntPtr BuildSingleEntryWatchResult(ulong journalId, long nextUsn, string fileName, uint reason)
    {
        var entrySize = MftVolume.NativeUsnEntrySize;
        var entriesPtr = Marshal.AllocHGlobal(entrySize);
        new Span<byte>((void*)entriesPtr, entrySize).Clear();

        var ptr = (byte*)entriesPtr;
        *(ulong*)ptr = 42;                  // recordNumber
        *(ulong*)(ptr + 8) = 5;            // parentRecordNumber
        *(long*)(ptr + 16) = nextUsn - 50; // usn
        *(long*)(ptr + 24) = 0;            // timestamp
        *(uint*)(ptr + 32) = reason;        // reason
        *(uint*)(ptr + 36) = 0x20;         // fileAttributes = Archive
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
}
