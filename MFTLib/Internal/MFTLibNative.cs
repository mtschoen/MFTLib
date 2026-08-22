using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    // .NET's DllImport resolver adds the right platform suffix:
    //   Windows -> MFTLibNative.dll
    //   Linux   -> libMFTLibNative.so
    const string LibraryName = "MFTLibNative";

    internal const uint ExpectedMftNativeAbiVersion = 2;
    internal const uint NativeCompactEntrySize = 32;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NativeMftProgressCallback(ulong recordsScanned, ulong totalRecords, double elapsedMs,
        IntPtr context);

    // Swappable function pointers - default to the native P/Invoke implementations.
    // Tests or platforms without the native library can replace these.
    internal static Func<uint> GetMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
    internal static Func<SafeHandle, string?, MatchFlags, uint, IntPtr> ParseMFTRecords = NativeParseMFTRecords;
    internal static Func<SafeHandle, string?, MatchFlags, uint, NativeMftProgressCallback?, IntPtr, IntPtr>
        ParseMFTRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
    internal static Action<IntPtr> FreeMftResult = NativeFreeMftResult;
    internal static Func<string, ulong, uint, bool> GenerateSyntheticMFT = NativeGenerateSyntheticMFT;
    internal static Func<string, ulong, uint, uint, bool> GenerateSyntheticMFTSized = NativeGenerateSyntheticMFTSized;
    internal static Func<string, string?, MatchFlags, uint, IntPtr> ParseMFTFromFile = NativeParseMFTFromFile;
    internal static Func<SafeHandle, IntPtr> QueryUsnJournal = NativeQueryUsnJournal;
    internal static Action<IntPtr> FreeUsnJournalInfo = NativeFreeUsnJournalInfo;
    internal static Func<SafeHandle, long, ulong, IntPtr> ReadUsnJournal = NativeReadUsnJournal;
    internal static Action<IntPtr> FreeUsnJournalResult = NativeFreeUsnJournalResult;
    internal static Func<SafeHandle, long, ulong, IntPtr> WatchUsnJournalBatch = NativeWatchUsnJournalBatch;
    internal static Func<SafeHandle, bool> CancelUsnJournalWatch = NativeCancelUsnJournalWatch;

    static IntPtr NativeParseMFTRecordsWithProgressDefault(
        SafeHandle volumeHandle, string? filter, MatchFlags matchFlags, uint bufferSizeRecords,
        NativeMftProgressCallback? callback, IntPtr context)
    {
        if (ParseMFTRecords != NativeParseMFTRecords)
        {
            return ParseMFTRecords(volumeHandle, filter, matchFlags, bufferSizeRecords);
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

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecordsWithProgress", CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Unicode)]
    internal static extern IntPtr NativeParseMFTRecordsWithProgressRaw(IntPtr volumeHandle, string? filter,
        uint matchFlags, uint bufferSizeRecords, NativeMftProgressCallback? callback, IntPtr context);

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
        var actual = GetMftNativeAbiVersion();
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
        GetMftNativeAbiVersion = NativeGetMftNativeAbiVersion;
        ParseMFTRecords = NativeParseMFTRecords;
        ParseMFTRecordsWithProgress = NativeParseMFTRecordsWithProgressDefault;
        FreeMftResult = NativeFreeMftResult;
        GenerateSyntheticMFT = NativeGenerateSyntheticMFT;
        GenerateSyntheticMFTSized = NativeGenerateSyntheticMFTSized;
        ParseMFTFromFile = NativeParseMFTFromFile;
        QueryUsnJournal = NativeQueryUsnJournal;
        FreeUsnJournalInfo = NativeFreeUsnJournalInfo;
        ReadUsnJournal = NativeReadUsnJournal;
        FreeUsnJournalResult = NativeFreeUsnJournalResult;
        WatchUsnJournalBatch = NativeWatchUsnJournalBatch;
        CancelUsnJournalWatch = NativeCancelUsnJournalWatch;
    }
}

