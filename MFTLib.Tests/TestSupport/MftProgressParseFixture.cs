using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

internal static unsafe class MftProgressParseFixture
{
    public static void ConfigureSingleRecordParse(Action<MFTLibNative.NativeMftProgressCallback?> reportProgress)
    {
        MFTLibNative._getMftNativeAbiVersion = () => MFTLibNative.ExpectedMftNativeAbiVersion;
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);

        var entryStride = (nuint)MFTLibNative.NativeCompactEntrySize;
        var entryBuffer = Marshal.AllocHGlobal((int)entryStride);
        new Span<byte>((void*)entryBuffer, (int)entryStride).Clear();
        Unsafe.WriteUnaligned((byte*)entryBuffer, 100UL);
        Unsafe.WriteUnaligned((byte*)entryBuffer + 28, (ushort)1);
        Unsafe.WriteUnaligned((byte*)entryBuffer + 30, (ushort)0);

        var parseResult = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = entryBuffer,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var parseResultPointer = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(parseResult, parseResultPointer, false);

        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, callback) =>
        {
            reportProgress(callback);
            return parseResultPointer;
        };
        MFTLibNative._freeMftResult = pointer =>
        {
            Marshal.FreeHGlobal(entryBuffer);
            Marshal.FreeHGlobal(pointer);
        };
    }
}
