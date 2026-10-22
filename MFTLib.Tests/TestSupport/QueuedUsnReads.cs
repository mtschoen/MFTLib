using System.Runtime.InteropServices;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Queues synthetic FSCTL_READ_USN_JOURNAL replies on the native USN hook and owns their memory until
///     released. The hook is process-wide, so users are nonparallel and call <see cref="Release" /> and
///     <c>NativeTestHooks.NativeResetTestState</c> in their cleanup.
/// </summary>
static class QueuedUsnReads
{
    static readonly List<IntPtr> Buffers = [];

    internal static unsafe void Queue(byte[] readBuffer)
    {
        var pointer = Marshal.AllocHGlobal(readBuffer.Length);
        Marshal.Copy(readBuffer, 0, pointer, readBuffer.Length);
        Buffers.Add(pointer);
        NativeTestHooks.NativeSetUsnIoSuccess((byte*)pointer, (uint)readBuffer.Length);
    }

    internal static void Release()
    {
        foreach (var buffer in Buffers)
        {
            Marshal.FreeHGlobal(buffer);
        }

        Buffers.Clear();
    }
}
