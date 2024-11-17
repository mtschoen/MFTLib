using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MFTLib;

public class MFTParseC
{
    // ReSharper disable InconsistentNaming
    const uint GENERIC_READ = 0x80000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;
    // ReSharper restore InconsistentNaming

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

        var volumeHandle = Kernel32.CreateFile(
            volume,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            throw new IOException($"Unable to open volume {volume}", Marshal.GetLastWin32Error());
        }

        return null;
    }
}