using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalEntryTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    [TestMethod]
    public void SequenceNumber_RoundTripsThroughCreate()
    {
        var entry = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 1197729,
            ParentRecordNumber = 5,
            SequenceNumber = 169,
            Usn = 42,
            Timestamp = new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
            Reason = UsnReason.FileCreate,
            FileAttributes = FileAttributes.Normal,
            FileName = "probe.txt"
        });

        Assert.AreEqual((ushort)169, entry.SequenceNumber);
        Assert.AreEqual(1197729ul, entry.RecordNumber);
    }

    [TestMethod]
    public async Task SequenceNumber_MarshalsFromNativeWatchEntry()
    {
        MFTLibNative._watchUsnJournalBatch = (_, _, _) => BuildSingleEntryWatchResult();
        MFTLibNative._cancelUsnJournalWatch = _ => true;
        MFTLibNative._freeUsnJournalResult = FreeWatchResult;
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);

        using var volume = MftVolume.Open("C");
        await foreach (var batch in volume.WatchUsnJournal(new UsnJournalCursor(1, 0)))
        {
            Assert.AreEqual(1, batch.Length);
            Assert.AreEqual(0x1246A1ul, batch[0].RecordNumber);
            Assert.AreEqual((ushort)0xA9, batch[0].SequenceNumber);
            break;
        }
    }

    static unsafe IntPtr BuildSingleEntryWatchResult()
    {
        const int entrySize = 564;
        var entriesPointer = Marshal.AllocHGlobal(entrySize);
        new Span<byte>((void*)entriesPointer, entrySize).Clear();

        var entryPointer = (byte*)entriesPointer;
        *(ulong*)entryPointer = 0x1246A1;
        *(ulong*)(entryPointer + 8) = 5;
        *(ushort*)(entryPointer + 562) = 0xA9;

        var nativeResult = new UsnJournalResultNative
        {
            EntryCount = 1,
            Entries = entriesPointer,
            NextUsn = 1,
            JournalId = 1
        };
        var resultPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalResultNative>());
        Marshal.StructureToPtr(nativeResult, resultPointer, false);
        return resultPointer;
    }

    static void FreeWatchResult(IntPtr resultPointer)
    {
        var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
        Marshal.FreeHGlobal(result.Entries);
        Marshal.FreeHGlobal(resultPointer);
    }
}
