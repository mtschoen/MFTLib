using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.NativeInterop;

static class FileUtilities
{
    internal static readonly VolumeHandleHooks Hooks = new();

    internal sealed class VolumeHandleHooks
    {
        internal Func<string, SafeFileHandle> _getVolumeHandle = NativeGetVolumeHandle;
        internal Func<string, SafeFileHandle> _getWatchVolumeHandle = NativeGetWatchVolumeHandle;

        internal void ResetToDefaults()
        {
            _getVolumeHandle = NativeGetVolumeHandle;
            _getWatchVolumeHandle = NativeGetWatchVolumeHandle;
        }
    }

    static SafeFileHandle NativeGetVolumeHandle(string volume) => OpenVolumeHandle(volume, 0);

    static SafeFileHandle NativeGetWatchVolumeHandle(string volume) => OpenVolumeHandle(volume, FILE_FLAG_OVERLAPPED);

    static SafeFileHandle OpenVolumeHandle(string volume, uint flags)
    {
        var volumeHandle = Kernel32.Hooks._createFile(
            volume,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            flags,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            throw new IOException($"Unable to open volume {volume}", Marshal.GetLastWin32Error());
        }

        return volumeHandle;
    }

    // ReSharper disable InconsistentNaming
    const uint GENERIC_READ = 0x80000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_SHARE_READ = 0x00000001;

    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    // ReSharper restore InconsistentNaming
}
