using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib;

static class MFTLibNative
{
    // .NET's DllImport resolver adds the right platform suffix:
    //   Windows -> MFTLibNative.dll
    //   Linux   -> libMFTLibNative.so
    const string LibraryName = "MFTLibNative";

    internal const uint ExpectedMftNativeAbiVersion = 6;
    internal const uint NativeCompactEntrySize = 52;

    // Swappable function pointers - default to the native P/Invoke implementations.
    // Tests or platforms without the native library can replace these.
    internal static Func<uint> _getMftNativeAbiVersion = NativeGetMftNativeAbiVersion;

    // The IntPtr after bufferSizeRecords is the caller's MftParseControl block, or zero for none.
    // The callback carries its own state, so the native context argument is always zero.
    internal static Func<SafeHandle, string?, MatchFlags, uint, IntPtr, NativeMftProgressCallback?, IntPtr>
        _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;

    internal static Action<IntPtr> _freeMftResult = NativeFreeMftResult;
    internal static Func<string, ulong, uint, uint, bool> _generateSyntheticMftSized = NativeGenerateSyntheticMFTSized;
    internal static Func<string, bool> _generateFixtureMft = NativeGenerateFixtureMFT;
    // Same trailing control and callback as _parseMftRecordsWithProgress; the callback context is always zero.
    internal static Func<string, string?, MatchFlags, uint, IntPtr, NativeMftProgressCallback?, IntPtr>
        _parseMftFromFile = NativeParseMFTFromFileDefault;
    internal static Func<SafeHandle, IntPtr> _queryUsnJournal = NativeQueryUsnJournal;
    internal static Action<IntPtr> _freeUsnJournalInfo = NativeFreeUsnJournalInfo;
    internal static Func<SafeHandle, long, ulong, uint, IntPtr> _readUsnJournal = NativeReadUsnJournal;
    internal static Action<IntPtr> _freeUsnJournalResult = NativeFreeUsnJournalResult;
    internal static Func<SafeHandle, long, ulong, SafeHandle, IntPtr> _watchUsnJournalBatchCancelable =
        NativeWatchUsnJournalBatchCancelable;
    internal static Func<SafeHandle, bool> _cancelUsnJournalWatch = NativeCancelUsnJournalWatch;

    static IntPtr NativeParseMFTRecordsWithProgressDefault(
        SafeHandle volumeHandle, string? filter, MatchFlags matchFlags, uint bufferSizeRecords, IntPtr control,
        NativeMftProgressCallback? callback)
    {
        return NativeParseMFTRecordsWithProgress(volumeHandle, filter, matchFlags, bufferSizeRecords, control, callback,
            IntPtr.Zero);
    }

    static IntPtr NativeParseMFTFromFileDefault(string filePath, string? filter, MatchFlags matchFlags,
        uint bufferSizeRecords, IntPtr control, NativeMftProgressCallback? callback)
    {
        return NativeParseMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords, control, callback, IntPtr.Zero);
    }

    // P/Invoke declarations (private - all access goes through the Func fields)
    [DllImport(LibraryName, EntryPoint = "GetMftNativeAbiVersion", CallingConvention = CallingConvention.Cdecl)]
    static extern uint NativeGetMftNativeAbiVersion();

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecordsWithProgress", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTRecordsWithProgress(SafeHandle volumeHandle, string? filter,
        MatchFlags matchFlags, uint bufferSizeRecords, IntPtr control, NativeMftProgressCallback? callback,
        IntPtr context);

    [DllImport(LibraryName, EntryPoint = "FreeMftResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeMftResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFTSized", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateSyntheticMFTSized(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize);

    [DllImport(LibraryName, EntryPoint = "GenerateFixtureMFT", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateFixtureMFT(string filePath);

    [DllImport(LibraryName, EntryPoint = "ParseMFTFromFile", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags,
        uint bufferSizeRecords, IntPtr control, NativeMftProgressCallback? callback, IntPtr context);

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
        _parseMftFromFile = NativeParseMFTFromFileDefault;
        _queryUsnJournal = NativeQueryUsnJournal;
        _freeUsnJournalInfo = NativeFreeUsnJournalInfo;
        _readUsnJournal = NativeReadUsnJournal;
        _freeUsnJournalResult = NativeFreeUsnJournalResult;
        _watchUsnJournalBatchCancelable = NativeWatchUsnJournalBatchCancelable;
        _cancelUsnJournalWatch = NativeCancelUsnJournalWatch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeMftProgressCallback(MftScanPhase phase, ulong recordsScanned, ulong totalRecords,
        double elapsedMs, IntPtr context);
}
