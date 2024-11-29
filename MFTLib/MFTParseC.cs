using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MFTLib;

public class MFTParseC
{
    public static MFTNode GetMFTNode(string volume)
    {
        if (string.IsNullOrEmpty(volume))
        {
            throw new ArgumentException("Volume name cannot be null or empty", nameof(volume));
        }

#if DEBUG
        var stopwatch = new Stopwatch();
        stopwatch.Start();
#endif

        var volumeHandle = FileUtilities.GetVolumeHandle(volume);

        return null;
    }

    public static void DumpVolumeInfo()
    {
        MFTLibC.PrintVolumeInfo();
    }
}