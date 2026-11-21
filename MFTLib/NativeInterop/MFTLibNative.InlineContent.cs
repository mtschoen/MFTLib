using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

static partial class MFTLibNative
{
    /// <summary>One native read of inline content: answers the call's requests from its start index on.</summary>
    internal delegate void ReadInlineContentFunction(MftInlineReaderHandle reader, ref MftInlineReadCall call,
        out MftInlineReadResult result);

    /// <summary>The smallest content buffer a native read accepts, <c>MFT_INLINE_MINIMUM_BUFFER_BYTES</c>.</summary>
    internal const int MinimumInlineContentBufferBytes = 65536;

    /// <summary>Opens a reader on the MFT of an NTFS volume or image; the volume handle must outlive the reader.</summary>
    internal static MftInlineReaderHandle OpenMftInlineReader(SafeHandle volumeHandle, out MftInlineReaderInfo info)
    {
        return NativeOpenMftInlineReader(volumeHandle, out info);
    }

    /// <summary>Opens a reader on an opened dump's records; the dump input must outlive the reader.</summary>
    internal static MftInlineReaderHandle OpenMftDumpInlineReader(MftDumpInputHandle input,
        out MftInlineReaderInfo info)
    {
        return NativeOpenMftDumpInlineReader(input, out info);
    }

    static void NativeReadMftInlineContentDefault(MftInlineReaderHandle reader, ref MftInlineReadCall call,
        out MftInlineReadResult result)
    {
        NativeReadMftInlineContent(reader, ref call, out result);
    }

    [LibraryImport(LibraryName, EntryPoint = "OpenMftInlineReader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial MftInlineReaderHandle NativeOpenMftInlineReader(SafeHandle volumeHandle,
        out MftInlineReaderInfo info);

    [LibraryImport(LibraryName, EntryPoint = "OpenMftDumpInlineReader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial MftInlineReaderHandle NativeOpenMftDumpInlineReader(MftDumpInputHandle input,
        out MftInlineReaderInfo info);

    [LibraryImport(LibraryName, EntryPoint = "ReadMftInlineContent")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void NativeReadMftInlineContent(MftInlineReaderHandle reader,
        ref MftInlineReadCall call, out MftInlineReadResult result);

    [LibraryImport(LibraryName, EntryPoint = "CloseMftInlineReader")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CloseMftInlineReader(IntPtr reader);
}
