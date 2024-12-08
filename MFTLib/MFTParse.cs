using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MFTLib;

public class MFTParse
{
    public static void ParseMFT(SafeHandle volumeHandle)
    {
        if (volumeHandle.IsInvalid)
            throw new ArgumentException("Volume handle is invalid.", nameof(volumeHandle));

#if DEBUG
        var stopwatch = new Stopwatch();
        stopwatch.Start();
#endif

        if (!MFTLibNative.ParseMFT(volumeHandle))
            throw new InvalidOperationException("Failed to parse MFT.");

#if DEBUG
        Console.WriteLine($"Parsed in {stopwatch.Elapsed}");
#endif
    }

    public static void DumpVolumeInfo(SafeHandle volumeHandle)
    {
        if (volumeHandle.IsInvalid)
            throw new ArgumentException("Volume handle is invalid.", nameof(volumeHandle));

        MFTLibNative.PrintVolumeInfo(volumeHandle);
    }
}