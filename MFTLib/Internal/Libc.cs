using System.Runtime.InteropServices;

// Declared in MFTLib.Index, not MFTLib: the index namespace may not depend on the flat one.
namespace MFTLib.Index;

static class Libc
{
    /// <summary>
    ///     The <c>MS_SYNC</c> flag for <see cref="msync" />, whose value differs by platform (Linux 4,
    ///     macOS 0x10; 4 is <c>MS_KILLPAGES</c> on macOS). Any other platform is refused rather than
    ///     passed a guessed value.
    /// </summary>
    internal static int SelectSynchronousFlag(OSPlatform platform)
    {
        if (platform == OSPlatform.Linux)
        {
            return 4;
        }

        if (platform == OSPlatform.OSX)
        {
            return 0x10;
        }

        throw new PlatformNotSupportedException($"No msync flag is known for platform {platform}.");
    }

    [DllImport("libc", SetLastError = true)]
    internal static extern unsafe int msync(byte* address, nuint length, int flags);
}
