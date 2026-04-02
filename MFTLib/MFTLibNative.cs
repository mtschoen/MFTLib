using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    private const string LibraryName = "MFTLibNative.dll";

    // P/Invoke declarations (private — all access goes through the Func fields)
    [DllImport(LibraryName, EntryPoint = "ParseMFTRecords", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr NativeParseMFTRecords(SafeHandle volumeHandle, string? filter, uint matchFlags, uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "FreeMftResult", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeFreeMftResult(IntPtr result);

    [DllImport(LibraryName, EntryPoint = "GenerateSyntheticMFT", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern bool NativeGenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords);

    [DllImport(LibraryName, EntryPoint = "ParseMFTFromFile", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr NativeParseMFTFromFile(string filePath, string? filter, uint matchFlags, uint bufferSizeRecords);

    // Swappable function pointers — default to the native P/Invoke implementations.
    // Tests or platforms without the native library can replace these.
    internal static Func<SafeHandle, string?, uint, uint, IntPtr> ParseMFTRecords = NativeParseMFTRecords;
    internal static Action<IntPtr> FreeMftResult = NativeFreeMftResult;
    internal static Func<string, ulong, uint, bool> GenerateSyntheticMFT = NativeGenerateSyntheticMFT;
    internal static Func<string, string?, uint, uint, IntPtr> ParseMFTFromFile = NativeParseMFTFromFile;

    /// <summary>
    /// Reset all function pointers to their native P/Invoke defaults.
    /// </summary>
    internal static void ResetToDefaults()
    {
        ParseMFTRecords = NativeParseMFTRecords;
        FreeMftResult = NativeFreeMftResult;
        GenerateSyntheticMFT = NativeGenerateSyntheticMFT;
        ParseMFTFromFile = NativeParseMFTFromFile;
    }
}
