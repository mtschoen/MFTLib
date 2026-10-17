using System.Runtime.InteropServices;
using System.Text;

namespace MFTLib.NativeInterop;

static class MFTLibNative
{
    // .NET's DllImport resolver adds the right platform suffix:
    //   Windows -> MFTLibNative.dll
    //   Linux   -> libMFTLibNative.so
    const string LibraryName = "MFTLibNative";

    internal const uint ExpectedMftNativeAbiVersion = 9;
    internal const uint NativeCompactEntrySize = 52;

    internal static readonly NativeHooks Hooks = new();

    internal sealed class NativeHooks
    {
        internal Func<uint> GetMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
        internal Func<SafeHandle, bool, uint, IntPtr, NativeMftProgressCallback?, IntPtr>
            ParseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
        internal Action<IntPtr> FreeMftResult = NativeFreeMftResult;

        internal void ResetToDefaults()
        {
            GetMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
            ParseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
            FreeMftResult = NativeFreeMftResult;
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
    [DllImport(LibraryName, EntryPoint = "GetMftNativeAbiVersion", CallingConvention = CallingConvention.Cdecl)]
    static extern uint NativeGetMftNativeAbiVersion();

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecordsWithProgress", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeParseMFTRecordsWithProgress(SafeHandle volumeHandle, uint includeFreed,
        uint bufferSizeRecords, IntPtr control, NativeMftProgressCallback? callback);

    [DllImport(LibraryName, EntryPoint = "FreeMftResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeMftResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFTSizedUtf8", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateSyntheticMFTSizedUtf8(byte[] filePathUtf8, ulong recordCount,
        uint bufferSizeRecords, uint recordSize);

    // The dump input exports exist on every platform and take the path as null-terminated UTF-8
    // bytes. They have no swappable field: a dump is exercised with real files, never a
    // substituted native call.
    [DllImport(LibraryName, EntryPoint = "OpenMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern MftDumpInputHandle OpenMftDumpInput(byte[] filePathUtf8, out MftDumpInputInfo info);

    [DllImport(LibraryName, EntryPoint = "ParseMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ParseMftDumpInput(MftDumpInputHandle input, uint bufferSizeRecords, IntPtr control,
        NativeMftProgressCallback? callback);

    [DllImport(LibraryName, EntryPoint = "CloseMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseMftDumpInput(IntPtr input);

    [DllImport(LibraryName, EntryPoint = "ReadUsnJournal", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NativeReadUsnJournal(SafeHandle volumeHandle, long startUsn, ulong journalIdentifier,
        uint maximumBufferReads);

    [DllImport(LibraryName, EntryPoint = "FreeUsnJournalResult", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeFreeUsnJournalResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "WatchUsnJournalBatchCancelable", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NativeWatchUsnJournalBatchCancelable(SafeHandle volumeHandle, long startUsn, ulong journalIdentifier,
        SafeHandle cancellationEvent);

    [DllImport(LibraryName, EntryPoint = "CancelUsnJournalWatch", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool NativeCancelUsnJournalWatch(SafeHandle volumeHandle);

    internal static void EnsureCompatibleNativeAbi()
    {
        var actual = Hooks.GetMftNativeAbiVersion();
        if (actual != ExpectedMftNativeAbiVersion)
        {
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects {ExpectedMftNativeAbiVersion}, native reports {actual}.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeMftProgressCallback(ulong recordsScanned, ulong totalRecords, double elapsedMs);
}
