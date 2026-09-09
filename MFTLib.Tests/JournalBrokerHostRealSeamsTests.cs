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
public partial class JournalBrokerHostRealSeamsTests : BrokerBlockTestBase
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

    static async Task<List<BrokerFrame>> ServeDefaultScanAsync(RecordingBlockSectionWriter writer)
    {
        MFTLibNative._readUsnJournal = (_, nextUsn, journalId) => BuildEmptyWatchResult(journalId, nextUsn + 100);
        MFTLibNative._freeUsnJournalResult = Marshal.FreeHGlobal;
        var (client, server) = DuplexStream.CreatePair();
        await using var clientLifetime = client;
        await using var serverLifetime = server;
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:section-C");
        await client.WriteAsync(request.WrittenMemory);
        await JournalBrokerHost.CreateDefault().ServeAsync(server, writer, true, CancellationToken.None);
        await server.DisposeAsync();
        using var response = new MemoryStream();
        await client.CopyToAsync(response);
        var bytes = response.ToArray();
        var frames = new List<BrokerFrame>();
        for (var offset = 0; offset < bytes.Length;)
        {
            frames.Add(BrokerProtocol.ReadFrame(bytes.AsSpan(offset), out var consumed));
            offset += consumed;
        }

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Error));
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

}
