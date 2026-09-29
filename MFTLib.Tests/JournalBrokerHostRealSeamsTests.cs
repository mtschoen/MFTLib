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
///     delegates JournalBrokerHostTests injects directly. Requests reach the host as raw frames
///     through <see cref="HostChannelHarness" />.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostRealSeamsTests
{
    const uint DefaultRecordSize = 1024;

    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
    }

    static SafeFileHandle FakeHandle()
    {
        return new SafeFileHandle(new IntPtr(1), false);
    }

    static void MockWatchJournalTip()
    {
        MFTLibNative._queryUsnJournal = _ =>
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalInfoNative>());
            Marshal.StructureToPtr(new UsnJournalInfoNative { JournalId = 7, NextUsn = 200 }, pointer, false);
            return pointer;
        };
        MFTLibNative._freeUsnJournalInfo = Marshal.FreeHGlobal;
    }

    // A host scan sizes its chunks from FSCTL_GET_NTFS_VOLUME_DATA before it opens the parse.
    static void MockVolumeRecordSize()
    {
        FileUtilities._getVolumeHandle = _ => FakeHandle();
        Kernel32._deviceIoControl = (_, _, _, _, outBuffer, _, out bytesReturned, _) =>
        {
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative
            {
                MftValidDataLength = 409_600,
                BytesPerFileRecordSegment = DefaultRecordSize
            }, outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
    }

    static void MockFreeMftResult()
    {
        MFTLibNative._freeMftResult = ptr =>
        {
            var parsed = Marshal.PtrToStructure<MftParseResult>(ptr);
            if (parsed.Entries != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.Entries);
            }

            if (parsed.EntryStrings != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parsed.EntryStrings);
            }

            Marshal.FreeHGlobal(ptr);
        };
    }

    // Native filename entries include an in-use file, an unused file, and an empty name.
    static unsafe IntPtr BuildThreeNameRecordsResult()
    {
        var stride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = Marshal.AllocHGlobal((int)stride * 3);
        new Span<byte>((void*)entryBuf, (int)stride * 3).Clear();

        var keptName = "file0.txt";
        var skippedName = "skip.txt";
        var totalUnits = keptName.Length + skippedName.Length;
        var stringBuf = Marshal.AllocHGlobal(totalUnits * sizeof(char));
        var stringSpan = new Span<char>((void*)stringBuf, totalUnits);
        keptName.AsSpan().CopyTo(stringSpan);
        skippedName.AsSpan().CopyTo(stringSpan.Slice(keptName.Length));

        var kept = (byte*)entryBuf;
        Unsafe.WriteUnaligned(kept, 100UL);
        Unsafe.WriteUnaligned(kept + 8, 5UL);
        Unsafe.WriteUnaligned(kept + 16, 0UL); // stringOffset
        Unsafe.WriteUnaligned(kept + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(kept + 28, (ushort)1); // InUse, not directory
        Unsafe.WriteUnaligned(kept + 30, (ushort)keptName.Length);

        var notInUse = (byte*)entryBuf + stride;
        Unsafe.WriteUnaligned(notInUse, 101UL);
        Unsafe.WriteUnaligned(notInUse + 8, 5UL);
        Unsafe.WriteUnaligned(notInUse + 16, (ulong)keptName.Length); // stringOffset
        Unsafe.WriteUnaligned(notInUse + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(notInUse + 28, (ushort)0); // not in use
        Unsafe.WriteUnaligned(notInUse + 30, (ushort)skippedName.Length);

        var emptyName = (byte*)entryBuf + 2 * stride;
        Unsafe.WriteUnaligned(emptyName, 102UL);
        Unsafe.WriteUnaligned(emptyName + 8, 5UL);
        Unsafe.WriteUnaligned(emptyName + 16, (ulong)totalUnits); // stringOffset
        Unsafe.WriteUnaligned(emptyName + 24, (uint)FileAttributes.Normal);
        Unsafe.WriteUnaligned(emptyName + 28, (ushort)1); // in use, but zero-length name
        Unsafe.WriteUnaligned(emptyName + 30, (ushort)0);

        var result = new MftParseResult
        {
            TotalRecords = 3,
            UsedRecords = 3,
            Entries = entryBuf,
            EntryStrings = stringBuf,
            EntryStringUnits = (ulong)totalUnits,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    // Scans drive C through the default host over a raw ArmAndScan frame and returns every frame
    // the drive pipe carried. The default host reads the catch-up journal through the real seam.
    static async Task<List<BrokerFrame>> ServeDefaultScanAsync(RecordingBlockSectionWriter writer)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Host scans size their chunks from FSCTL_GET_NTFS_VOLUME_DATA, which requires Windows.");
        }

        MockVolumeRecordSize();
        MFTLibNative._readUsnJournal = (_, nextUsn, journalId, _) => BuildEmptyWatchResult(journalId, nextUsn + 100);
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;
        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault(), writer);

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C', "section-C"));

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error),
            string.Join("; ", frames.Where(frame => frame.Kind == BrokerFrameKind.Error).Select(frame => frame.Message)));
        return frames;
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
}
