using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MFTLib.NativeInterop;

static partial class MFTLibNative
{
    // .NET's DllImport resolver adds the right platform suffix:
    //   Windows -> MFTLibNative.dll
    //   Linux   -> libMFTLibNative.so
    const string LibraryName = "MFTLibNative";

    internal const uint ExpectedMftNativeAbiVersion = 10;
    internal const uint NativeCompactEntrySize = 52;

    internal static readonly NativeHooks Hooks = new();

    internal sealed class NativeHooks
    {
        internal Func<uint> _getMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
        internal Func<SafeHandle, bool, uint, IntPtr, NativeMftProgressCallback?, IntPtr>
            _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
        internal Action<IntPtr> _freeMftResult = NativeFreeMftResult;

        internal void ResetToDefaults()
        {
            _getMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
            _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
            _freeMftResult = NativeFreeMftResult;
        }
    }

    static IntPtr NativeParseMFTRecordsWithProgressDefault(
        SafeHandle volumeHandle, bool includeFreed, uint bufferSizeRecords, IntPtr control,
        NativeMftProgressCallback? callback)
    {
        return NativeParseMFTRecordsWithProgress(volumeHandle, includeFreed ? 1u : 0u, bufferSizeRecords, control,
            callback);
    }

    internal static bool GenerateSyntheticMftSized(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize)
    {
        return NativeGenerateSyntheticMFTSizedUtf8(NullTerminatedUtf8(filePath), recordCount, bufferSizeRecords,
            recordSize);
    }

    // The UTF-8 exports read the path up to its first null byte.
    internal static byte[] NullTerminatedUtf8(string path)
    {
        return Encoding.UTF8.GetBytes(path + '\0');
    }

    // P/Invoke declarations (private - all access goes through the Func fields)
    [LibraryImport(LibraryName, EntryPoint = "GetMftNativeAbiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint NativeGetMftNativeAbiVersion();

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecordsWithProgress", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeParseMFTRecordsWithProgress(SafeHandle volumeHandle, uint includeFreed,
        uint bufferSizeRecords, IntPtr control, NativeMftProgressCallback? callback);

    [LibraryImport(LibraryName, EntryPoint = "FreeMftResult")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void NativeFreeMftResult(IntPtr result);

    [LibraryImport(LibraryName, EntryPoint = "GenerateSyntheticMFTSizedUtf8")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool NativeGenerateSyntheticMFTSizedUtf8(byte[] filePathUtf8, ulong recordCount,
        uint bufferSizeRecords, uint recordSize);

    // The dump input exports exist on every platform and take the path as null-terminated UTF-8
    // bytes. They have no swappable field: a dump is exercised with real files, never a
    // substituted native call.
    [DllImport(LibraryName, EntryPoint = "OpenMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern MftDumpInputHandle OpenMftDumpInput(byte[] filePathUtf8, out MftDumpInputInfo info);

    [DllImport(LibraryName, EntryPoint = "ParseMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ParseMftDumpInput(MftDumpInputHandle input, uint bufferSizeRecords, IntPtr control,
        NativeMftProgressCallback? callback);

    [LibraryImport(LibraryName, EntryPoint = "CloseMftDumpInput")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CloseMftDumpInput(IntPtr input);

    [LibraryImport(LibraryName, EntryPoint = "ReadUsnJournal")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr NativeReadUsnJournal(SafeHandle volumeHandle, long startUsn, ulong journalIdentifier,
        uint maximumBufferReads);

    [LibraryImport(LibraryName, EntryPoint = "FreeUsnJournalResult")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void NativeFreeUsnJournalResult(IntPtr result);

    [LibraryImport(LibraryName, EntryPoint = "WatchUsnJournalBatchCancelable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr NativeWatchUsnJournalBatchCancelable(SafeHandle volumeHandle, long startUsn, ulong journalIdentifier,
        SafeHandle cancellationEvent);

    [LibraryImport(LibraryName, EntryPoint = "CancelUsnJournalWatch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool NativeCancelUsnJournalWatch(SafeHandle volumeHandle);

    internal static void EnsureCompatibleNativeAbi()
    {
        var actual = Hooks._getMftNativeAbiVersion();
        if (actual != ExpectedMftNativeAbiVersion)
        {
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects {ExpectedMftNativeAbiVersion}, native reports {actual}.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeMftProgressCallback(ulong recordsScanned, ulong totalRecords, double elapsedMs);
}
