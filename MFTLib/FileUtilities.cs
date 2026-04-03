using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace MFTLib;

static class FileUtilities
{
    // ReSharper disable InconsistentNaming
    const uint GENERIC_READ = 0x80000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    // ReSharper restore InconsistentNaming

    internal static SafeFileHandle GetVolumeHandle(string volume)
    {
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

        return volumeHandle;
    }
}
