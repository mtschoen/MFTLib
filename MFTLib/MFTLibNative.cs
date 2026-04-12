using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    const string LibraryName = "MFTLibNative.dll";

    // P/Invoke declarations (private — all access goes through the Func fields)
    [DllImport(LibraryName, EntryPoint = "ParseMFTRecords", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTRecords(SafeHandle volumeHandle, string? filter, MatchFlags matchFlags, uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "FreeMftResult", CallingConvention = CallingConvention.Cdecl)]
    static extern void NativeFreeMftResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFT", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern bool NativeGenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "ParseMFTFromFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern IntPtr NativeParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags, uint bufferSizeRecords);

    // Test support exports — control native failure injection
    [DllImport(LibraryName, EntryPoint = "SetMaxThreads", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetMaxThreads(uint maxThreads);

    [DllImport(LibraryName, EntryPoint = "SetAllocFailCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetAllocFailCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetReadFailCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetReadFailCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetUsnIoFailError", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetUsnIoFailError(uint error, int countdown);

    [DllImport(LibraryName, EntryPoint = "ResetTestState", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeResetTestState();

    // ParseMFTRecords overload that takes raw IntPtr handle (for testing with invalid handles)
    [DllImport(LibraryName, EntryPoint = "ParseMFTRecords", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr NativeParseMFTRecordsRaw(IntPtr volumeHandle, string? filter, uint matchFlags, uint bufferSizeRecords);

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

    // Swappable function pointers — default to the native P/Invoke implementations.
    // Tests or platforms without the native library can replace these.
    internal static Func<SafeHandle, string?, MatchFlags, uint, IntPtr> ParseMFTRecords = NativeParseMFTRecords;
    internal static Action<IntPtr> FreeMftResult = NativeFreeMftResult;
    internal static Func<string, ulong, uint, bool> GenerateSyntheticMFT = NativeGenerateSyntheticMFT;
    internal static Func<string, string?, MatchFlags, uint, IntPtr> ParseMFTFromFile = NativeParseMFTFromFile;
    internal static Func<SafeHandle, IntPtr> QueryUsnJournal = NativeQueryUsnJournal;
    internal static Action<IntPtr> FreeUsnJournalInfo = NativeFreeUsnJournalInfo;
    internal static Func<SafeHandle, long, ulong, IntPtr> ReadUsnJournal = NativeReadUsnJournal;
    internal static Action<IntPtr> FreeUsnJournalResult = NativeFreeUsnJournalResult;
    internal static Func<SafeHandle, long, ulong, IntPtr> WatchUsnJournalBatch = NativeWatchUsnJournalBatch;
    internal static Func<SafeHandle, bool> CancelUsnJournalWatch = NativeCancelUsnJournalWatch;

    /// <summary>
    /// Reset all function pointers to their native P/Invoke defaults.
    /// </summary>
    internal static void ResetToDefaults()
    {
        ParseMFTRecords = NativeParseMFTRecords;
        FreeMftResult = NativeFreeMftResult;
        GenerateSyntheticMFT = NativeGenerateSyntheticMFT;
        ParseMFTFromFile = NativeParseMFTFromFile;
        QueryUsnJournal = NativeQueryUsnJournal;
        FreeUsnJournalInfo = NativeFreeUsnJournalInfo;
        ReadUsnJournal = NativeReadUsnJournal;
        FreeUsnJournalResult = NativeFreeUsnJournalResult;
        WatchUsnJournalBatch = NativeWatchUsnJournalBatch;
        CancelUsnJournalWatch = NativeCancelUsnJournalWatch;
    }
}
