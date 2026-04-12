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
    public void UsnJournalCursor_StoresValues()
    {
        var cursor = new UsnJournalCursor(0xDEADBEEF, 12345);
        Assert.AreEqual(0xDEADBEEFUL, cursor.JournalId);
        Assert.AreEqual(12345L, cursor.NextUsn);
    }
}
