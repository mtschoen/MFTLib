using System.Runtime.InteropServices;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     P/Invoke declarations for the native test hooks and the raw-handle parse entry used by native coverage tests.
///     The native exports are test-only and unsupported; product code never calls them.
/// </summary>
internal static class NativeTestHooks
{
    const string LibraryName = "MFTLibNative";

    // Native test-only exports (MFTLibNative/core/test_hooks.cpp): failure injection and observation.
    [DllImport(LibraryName, EntryPoint = "SetMaxThreads", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetMaxThreads(uint maxThreads);

    [DllImport(LibraryName, EntryPoint = "SetAllocFailCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetAllocFailCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetReadFailCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetReadFailCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "SetFailFileSize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetFailFileSize(int fail);

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

    [DllImport(LibraryName, EntryPoint = "SetUsnWatchPipe", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetUsnWatchPipe(SafeHandle handle, SafeHandle beforeIssue,
        SafeHandle continueIssue, SafeHandle issued, int gateReadNumber);

    [DllImport(LibraryName, EntryPoint = "SetCancelCheckCountdown", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeSetCancelCheckCountdown(int countdown);

    [DllImport(LibraryName, EntryPoint = "GetNativeHardwareThreadCount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint NativeGetNativeHardwareThreadCount();

    [DllImport(LibraryName, EntryPoint = "GetChunkThreadCounts", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe uint NativeGetChunkThreadCounts(uint* counts, uint capacity);

    [DllImport(LibraryName, EntryPoint = "ResetTestState", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void NativeResetTestState();

    [DllImport(LibraryName, EntryPoint = "ParseMFTRecordsWithProgress", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr NativeParseMFTRecordsWithProgressRaw(IntPtr volumeHandle, uint includeFreed,
        uint bufferSizeRecords, IntPtr control, IntPtr callback, IntPtr context);

    // The dump input exports with raw pointers, for calls the typed bindings cannot express:
    // no info block, no input, and a zero buffer size.
    [DllImport(LibraryName, EntryPoint = "OpenMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NativeOpenMftDumpInputRaw(byte[] filePathUtf8, IntPtr info);

    [DllImport(LibraryName, EntryPoint = "ParseMftDumpInput", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr NativeParseMftDumpInputRaw(IntPtr input, uint bufferSizeRecords, IntPtr control,
        IntPtr callback, IntPtr context);

    // Parse entry that takes a raw IntPtr handle and no progress control (for testing with invalid handles)
    internal static IntPtr NativeParseMFTRecordsRaw(IntPtr volumeHandle, uint bufferSizeRecords)
    {
        return NativeParseMFTRecordsWithProgressRaw(volumeHandle, 0, bufferSizeRecords, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero);
    }
}
