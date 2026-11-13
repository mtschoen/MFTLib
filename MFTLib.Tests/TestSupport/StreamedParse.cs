using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Lets a test that substitutes a whole-result volume parse drive the streaming parse the producers run: the
///     substituted parse runs, then a successful result's entries go to the sink as one slice, as the native parse
///     hands over its slices, followed by an empty set of extension data sizes.
/// </summary>
static unsafe class StreamedParse
{
    /// <summary>Installs <paramref name="wholeParse" /> behind the streaming hook.</summary>
    internal static void Install(Func<SafeHandle, bool, uint, IntPtr, NativeProgressCallback, IntPtr> wholeParse)
    {
        MFTLibNative.Hooks._parseMftRecordsStreaming = From(wholeParse);
    }

    /// <summary>The streaming hook that replays what <paramref name="wholeParse" /> returns.</summary>
    internal static Func<SafeHandle, bool, uint, IntPtr, NativeProgressCallback, IntPtr, IntPtr> From(
        Func<SafeHandle, bool, uint, IntPtr, NativeProgressCallback, IntPtr> wholeParse) =>
        (volumeHandle, includeFreed, bufferSizeRecords, control, progress, sinkAddress) =>
        {
            var resultAddress = wholeParse(volumeHandle, includeFreed, bufferSizeRecords, control, progress);
            if (resultAddress == IntPtr.Zero)
            {
                return resultAddress;
            }

            var result = Marshal.PtrToStructure<MftParseResult>(resultAddress);
            if (result.Cancelled != 0 || !string.IsNullOrEmpty(result.ErrorMessage))
            {
                return resultAddress;
            }

            var sink = *(MftParseSink*)sinkAddress;
            var stopped = result.UsedRecords > 0 &&
                          ((delegate* unmanaged[Cdecl]<IntPtr, byte*, ulong, ushort*, ulong, int>)sink.Slice)(
                              sink.Context, (byte*)result.Entries, result.UsedRecords, (ushort*)result.EntryStrings,
                              result.EntryStringUnits) != 0;
            stopped = stopped ||
                      ((delegate* unmanaged[Cdecl]<IntPtr, MftExtensionDataSize*, ulong, int>)sink.Extensions)(
                          sink.Context, null, 0) != 0;
            if (stopped)
            {
                // A sink that stops the parse makes it report cancelled, as the native parse does.
                Marshal.WriteInt32(resultAddress, (int)Marshal.OffsetOf<MftParseResult>(nameof(MftParseResult.Cancelled)), 1);
            }

            return resultAddress;
        };
}
