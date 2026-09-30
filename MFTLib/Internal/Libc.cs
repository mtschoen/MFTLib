using System.Runtime.InteropServices;

// Declared in MFTLib.Index, not MFTLib: the index namespace may not depend on the flat one.
namespace MFTLib.Index;

static class Libc
{
    /// <summary>
    ///     The native <c>MS_SYNC | MS_INVALIDATE</c> flags for <see cref="msync" />:
    ///     Linux 6 (4 | 2), macOS 0x12 (0x10 | 0x02). On macOS, 4 is
    ///     <c>MS_KILLPAGES</c>. Any other platform is refused rather than passed guessed flags.
    /// </summary>
    internal static int SelectSynchronousFlag(OSPlatform platform)
    {
        if (platform == OSPlatform.Linux)
        {
            return 4 | 2;
        }

        if (platform == OSPlatform.OSX)
        {
            return 0x10 | 0x02;
        }

        throw new PlatformNotSupportedException($"No msync flag is known for platform {platform}.");
    }

    [DllImport("libc", SetLastError = true)]
    internal static extern unsafe int msync(byte* address, nuint length, int flags);
}
