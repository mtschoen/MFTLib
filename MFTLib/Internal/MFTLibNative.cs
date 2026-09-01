using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    // .NET's DllImport resolver adds the right platform suffix:
    //   Windows -> MFTLibNative.dll
    //   Linux   -> libMFTLibNative.so
    const string LibraryName = "MFTLibNative";

    internal const uint ExpectedMftNativeAbiVersion = 3;
    internal const uint NativeCompactEntrySize = 32;

    // Swappable function pointers - default to the native P/Invoke implementations.
    // Tests or platforms without the native library can replace these.
    internal static Func<uint> _getMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
    internal static Func<SafeHandle, string?, MatchFlags, uint, IntPtr> _parseMftRecords = NativeParseMFTRecords;

    internal static Func<SafeHandle, string?, MatchFlags, uint, NativeMftProgressCallback?, IntPtr, IntPtr>
        _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;

    internal static Action<IntPtr> _freeMftResult = NativeFreeMftResult;
    internal static Func<string, ulong, uint, bool> _generateSyntheticMft = NativeGenerateSyntheticMFT;
    internal static Func<string, ulong, uint, uint, bool> _generateSyntheticMftSized = NativeGenerateSyntheticMFTSized;
    internal static Func<string, string?, MatchFlags, uint, IntPtr> _parseMftFromFile = NativeParseMFTFromFile;
    internal static Func<SafeHandle, IntPtr> _queryUsnJournal = NativeQueryUsnJournal;
    internal static Action<IntPtr> _freeUsnJournalInfo = NativeFreeUsnJournalInfo;
    internal static Func<SafeHandle, long, ulong, IntPtr> _readUsnJournal = NativeReadUsnJournal;
    internal static Action<IntPtr> _freeUsnJournalResult = NativeFreeUsnJournalResult;
    internal static Func<SafeHandle, long, ulong, IntPtr> _watchUsnJournalBatch = NativeWatchUsnJournalBatch;
    internal static Func<SafeHandle, bool> _cancelUsnJournalWatch = NativeCancelUsnJournalWatch;

    static IntPtr NativeParseMFTRecordsWithProgressDefault(
        SafeHandle volumeHandle, string? filter, MatchFlags matchFlags, uint bufferSizeRecords,
        NativeMftProgressCallback? callback, IntPtr context)
    {
        if (_parseMftRecords != NativeParseMFTRecords)
        {
            return _parseMftRecords(volumeHandle, filter, matchFlags, bufferSizeRecords);
        }

        return NativeParseMFTRecordsWithProgress(volumeHandle, filter, matchFlags, bufferSizeRecords, callback, context);
    }

    // P/Invoke declarations (private - all access goes through the Func fields)
    [DllImport(LibraryName, EntryPoint = "GetMftNativeAbiVersion", CallingConvention = CallingConvention.Cdecl)]
    static extern uint NativeGetMftNativeAbiVersion();

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecords", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTRecords(SafeHandle volumeHandle, string? filter, MatchFlags matchFlags,
        uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecordsWithProgress", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTRecordsWithProgress(SafeHandle volumeHandle, string? filter,
        MatchFlags matchFlags, uint bufferSizeRecords, NativeMftProgressCallback? callback, IntPtr context);

    [DllImport(LibraryName, EntryPoint = "FreeMftResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeMftResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFT", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFTSized", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool NativeGenerateSyntheticMFTSized(string filePath, ulong recordCount, uint bufferSizeRecords,
        uint recordSize);

    [DllImport(LibraryName, EntryPoint = "ParseMFTFromFile", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags,
        uint bufferSizeRecords);

    // Test support exports - control native failure injection
    [DllImport(LibraryName, EntryPoint = "SetMaxThreads", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetMaxThreads(uint maxThreads);

    [DllImport(LibraryName, EntryPoint = "SetAllocFailCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetAllocFailCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetReadFailCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetReadFailCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetNamePoolCapacityOverride", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetNamePoolCapacityOverride(ulong bytes);

    [DllImport(LibraryName, EntryPoint = "SetFailFileSize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetFailFileSize(int fail);

    [DllImport(LibraryName, EntryPoint = "SetFailPathConversion", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetFailPathConversion(int fail);

    [DllImport(LibraryName, EntryPoint = "SetFailPlatformRead", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetFailPlatformRead(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetFailPlatformWrite", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetFailPlatformWrite(int fail);

    [DllImport(LibraryName, EntryPoint = "SetVolumeRecordSizeOverride", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetVolumeRecordSizeOverride(uint recordSize);

    [DllImport(LibraryName, EntryPoint = "SetUsnIoFailError", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetUsnIoFailError(uint error, int countdown);

    [DllImport(LibraryName, EntryPoint = "SetUsnIoSuccess", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe void NativeSetUsnIoSuccess(byte* data, uint size);

    [DllImport(LibraryName, EntryPoint = "SetUsnOverlappedAbort", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetUsnOverlappedAbort();

    [DllImport(LibraryName, EntryPoint = "ResetTestState", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeResetTestState();

    // ParseMFTRecords overload that takes raw IntPtr handle (for testing with invalid handles)
    [DllImport(LibraryName, EntryPoint = "ParseMFTRecords", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    internal static extern IntPtr NativeParseMFTRecordsRaw(IntPtr volumeHandle, string? filter, uint matchFlags,
        uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "QueryUsnJournal", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeQueryUsnJournal(SafeHandle volumeHandle);

    [DllImport(LibraryName, EntryPoint = "FreeUsnJournalInfo", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeUsnJournalInfo(IntPtr info);

    [DllImport(LibraryName, EntryPoint = "ReadUsnJournal", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeReadUsnJournal(SafeHandle volumeHandle, long startUsn, ulong journalId);

    [DllImport(LibraryName, EntryPoint = "FreeUsnJournalResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeUsnJournalResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "WatchUsnJournalBatch", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeWatchUsnJournalBatch(SafeHandle volumeHandle, long startUsn, ulong journalId);

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
        _parseMftRecords = NativeParseMFTRecords;
        _parseMftRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
        _freeMftResult = NativeFreeMftResult;
        _generateSyntheticMft = NativeGenerateSyntheticMFT;
        _generateSyntheticMftSized = NativeGenerateSyntheticMFTSized;
        _parseMftFromFile = NativeParseMFTFromFile;
        _queryUsnJournal = NativeQueryUsnJournal;
        _freeUsnJournalInfo = NativeFreeUsnJournalInfo;
        _readUsnJournal = NativeReadUsnJournal;
        _freeUsnJournalResult = NativeFreeUsnJournalResult;
        _watchUsnJournalBatch = NativeWatchUsnJournalBatch;
        _cancelUsnJournalWatch = NativeCancelUsnJournalWatch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeMftProgressCallback(MftScanPhase phase, ulong recordsScanned, ulong totalRecords,
        double elapsedMs, IntPtr context);
}
