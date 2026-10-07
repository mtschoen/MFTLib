using System.Runtime.InteropServices;
using System.Text;
using MFTLib.Interop;

namespace MFTLib;

static class MFTLibNative
{
    // .NET's DllImport resolver adds the right platform suffix:
    //   Windows -> MFTLibNative.dll
    //   Linux   -> libMFTLibNative.so
    const string LibraryName = "MFTLibNative";

    internal const uint ExpectedMftNativeAbiVersion = 7;
    internal const uint NativeCompactEntrySize = 52;

    // Swappable function pointers - default to the native P/Invoke implementations.
    // Tests or platforms without the native library can replace these.
    internal static Func<uint> _getMftNativeAbiVersion = NativeGetMftNativeAbiVersion;

    // The bool asks for freed base records too. The IntPtr after bufferSizeRecords is the caller's
    // MftParseControl block, or zero for none. The callback carries its own state, so the native
    // context argument is always zero.
    internal static Func<SafeHandle, bool, uint, IntPtr, NativeMftProgressCallback?, IntPtr>
        _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;

    internal static Action<IntPtr> _freeMftResult = NativeFreeMftResult;
    internal static Func<string, ulong, uint, uint, bool> _generateSyntheticMftSized = NativeGenerateSyntheticMFTSized;
    internal static Func<string, bool> _generateFixtureMft = NativeGenerateFixtureMFT;
    internal static Func<SafeHandle, IntPtr> _queryUsnJournal = NativeQueryUsnJournal;
    internal static Action<IntPtr> _freeUsnJournalInfo = NativeFreeUsnJournalInfo;
    internal static Func<SafeHandle, long, ulong, uint, IntPtr> _readUsnJournal = NativeReadUsnJournal;
    internal static Action<IntPtr> _freeUsnJournalResult = NativeFreeUsnJournalResult;
    internal static Func<SafeHandle, long, ulong, SafeHandle, IntPtr> _watchUsnJournalBatchCancelable =
        NativeWatchUsnJournalBatchCancelable;
    internal static Func<SafeHandle, bool> _cancelUsnJournalWatch = NativeCancelUsnJournalWatch;

    static IntPtr NativeParseMFTRecordsWithProgressDefault(
        SafeHandle volumeHandle, bool includeFreed, uint bufferSizeRecords, IntPtr control,
        NativeMftProgressCallback? callback)
    {
        return NativeParseMFTRecordsWithProgress(volumeHandle, includeFreed ? 1u : 0u, bufferSizeRecords, control,
            callback, IntPtr.Zero);
    }

    static bool NativeGenerateSyntheticMFTSized(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize)
    {
        return NativeGenerateSyntheticMFTSizedUtf8(NullTerminatedUtf8(filePath), recordCount, bufferSizeRecords,
            recordSize);
    }

    static bool NativeGenerateFixtureMFT(string filePath)
    {
        return NativeGenerateFixtureMFTUtf8(NullTerminatedUtf8(filePath));
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
        uint bufferSizeRecords, IntPtr control, NativeMftProgressCallback? callback, IntPtr context);

    [DllImport(LibraryName, EntryPoint = "FreeMftResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeMftResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFTSizedUtf8", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateSyntheticMFTSizedUtf8(byte[] filePathUtf8, ulong recordCount,
        uint bufferSizeRecords, uint recordSize);

    [DllImport(LibraryName, EntryPoint = "GenerateFixtureMFTUtf8", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateFixtureMFTUtf8(byte[] filePathUtf8);

    // The dump input exports exist on every platform and take the path as null-terminated UTF-8
    // bytes. They have no swappable field: a dump is exercised with real files, never a
    // substituted native call.
    [DllImport(LibraryName, EntryPoint = "OpenMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern MftDumpInputHandle OpenMftDumpInput(byte[] filePathUtf8, out MftDumpInputInfo info);

    [DllImport(LibraryName, EntryPoint = "ParseMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ParseMftDumpInput(MftDumpInputHandle input, uint bufferSizeRecords, IntPtr control,
        NativeMftProgressCallback? callback, IntPtr context);

    [DllImport(LibraryName, EntryPoint = "CloseMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseMftDumpInput(IntPtr input);

    [DllImport(LibraryName, EntryPoint = "QueryUsnJournal", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeQueryUsnJournal(SafeHandle volumeHandle);

    [DllImport(LibraryName, EntryPoint = "FreeUsnJournalInfo", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeUsnJournalInfo(IntPtr info);

    [DllImport(LibraryName, EntryPoint = "ReadUsnJournal", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeReadUsnJournal(SafeHandle volumeHandle, long startUsn, ulong journalId,
        uint maximumBufferReads);

    [DllImport(LibraryName, EntryPoint = "FreeUsnJournalResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeUsnJournalResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "WatchUsnJournalBatchCancelable", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeWatchUsnJournalBatchCancelable(SafeHandle volumeHandle, long startUsn, ulong journalId,
        SafeHandle cancellationEvent);

    [DllImport(LibraryName, EntryPoint = "CancelUsnJournalWatch", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool NativeCancelUsnJournalWatch(SafeHandle volumeHandle);

    internal static void EnsureCompatibleNativeAbi()
    {
        var actual = _getMftNativeAbiVersion();
        if (actual != ExpectedMftNativeAbiVersion)
        {
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects {ExpectedMftNativeAbiVersion}, native reports {actual}.");
        }
    }

    /// <summary>
    ///     Reset all function pointers to their native P/Invoke defaults.
    /// </summary>
    internal static void ResetToDefaults()
    {
        _getMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
        _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
        _freeMftResult = NativeFreeMftResult;
        _generateSyntheticMftSized = NativeGenerateSyntheticMFTSized;
        _generateFixtureMft = NativeGenerateFixtureMFT;
        _queryUsnJournal = NativeQueryUsnJournal;
        _freeUsnJournalInfo = NativeFreeUsnJournalInfo;
        _readUsnJournal = NativeReadUsnJournal;
        _freeUsnJournalResult = NativeFreeUsnJournalResult;
        _watchUsnJournalBatchCancelable = NativeWatchUsnJournalBatchCancelable;
        _cancelUsnJournalWatch = NativeCancelUsnJournalWatch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeMftProgressCallback(ulong recordsScanned, ulong totalRecords, double elapsedMs,
        IntPtr context);
}
