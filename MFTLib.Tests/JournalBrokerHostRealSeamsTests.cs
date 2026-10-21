using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Exercises the real production volume access <see cref="JournalBrokerHost.CreateDefault" />
///     wires up (MftVolume and UsnJournalVolume-backed query/scan/catch-up/watch), using the same non-admin
///     native-mock technique as MockVolumeTests / UsnJournalSyntheticTests, instead of the fake
///     volume access JournalBrokerHostTests injects directly. Requests reach the host as raw frames
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
        NativeTestHooks.NativeResetTestState();
        QueuedUsnReads.Release();
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        Kernel32.Hooks.ResetToDefaults();
        JournalQueryFake.Reset();
    }

    static void MockWatchJournalTip()
    {
        JournalQueryFake.Serve(journalIdentifier: 7, nextUsn: 200);
    }

    // A host scan sizes its chunks from FSCTL_GET_NTFS_VOLUME_DATA before it opens the parse.
    static void MockVolumeRecordSize()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var query = Kernel32.Hooks._deviceIoControl;
        Kernel32.Hooks._deviceIoControl = (device, code, input, inputSize, output, outputSize,
            out returned, overlapped) =>
        {
            if (code == UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                return query(device, code, input, inputSize, output, outputSize, out returned, overlapped);
            }
            Assert.AreEqual(0x00090064u, code);
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative
            {
                MftValidDataLength = 409_600,
                BytesPerFileRecordSegment = DefaultRecordSize
            }, output, false);
            returned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
    }

    static void MockFreeMftResult()
    {
        MFTLibNative.Hooks._freeMftResult = ptr =>
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
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);
        return resultPtr;
    }

    // Scans drive C through the default host over a raw ArmAndScan frame and returns every frame
    // the drive pipe carried. The default host reads the catch-up journal through the real seam.
    static async Task<List<BrokerFrame>> ServeDefaultScanAsync(RecordingBlockSectionWriter writer, long armedNextUsn)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Host scans size their chunks from FSCTL_GET_NTFS_VOLUME_DATA, which requires Windows.");
        }

        MockVolumeRecordSize();
        // The first bounded read advances 100 USNs past the armed cursor; the journal tip stays
        // there, so the next read returns the cursor unchanged and catch-up ends.
        for (var read = 0; read < 4; read++)
        {
            QueuedUsnReads.Queue(UsnReadBuffers.Build(armedNextUsn + 100));
        }

        await using var harness = new HostChannelHarness(JournalBrokerHost.CreateDefault(), writer);

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C', "section-C"));

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error),
            string.Join("; ", frames.Where(frame => frame.Kind == BrokerFrameKind.Error).Select(frame => frame.Message)));
        return frames;
    }

}
