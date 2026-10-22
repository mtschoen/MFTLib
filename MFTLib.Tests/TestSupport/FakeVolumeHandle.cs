using MFTLib.Index;
using MFTLib.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

/// <summary>A non-owning handle for tests whose volume I/O is served by test seams or native hooks.</summary>
static class FakeVolumeHandle
{
    internal static SafeFileHandle Create() => new(new IntPtr(1), false);

    /// <summary>
    ///     Opens a test volume and captures the handle supplied by its open hook.
    ///     The returned volume owns disposal; the out handle is borrowed for native assertions.
    /// </summary>
    internal static MftVolume OpenVolume(DriveKey drive, out SafeFileHandle volumeHandle)
    {
        SafeFileHandle capturedHandle = null!;
        FileUtilities.Hooks._getVolumeHandle = _ => capturedHandle = Create();
        var volume = MftVolume.Open(drive);
        volumeHandle = capturedHandle;
        return volume;
    }
}
