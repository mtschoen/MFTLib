using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

static partial class Libc
{
    /// <summary>
    ///     The native <c>MS_SYNC | MS_INVALIDATE</c> flags for <see cref="MemorySync" />:
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

    [LibraryImport("libc", EntryPoint = "msync", SetLastError = true)]
    internal static unsafe partial int MemorySync(byte* address, nuint length, int flags);

    /// <summary>The <c>MADV_DONTNEED</c> advice value, 4 on Linux and macOS alike.</summary>
    internal const int AdviseDontNeed = 4;

    [LibraryImport("libc", EntryPoint = "madvise", SetLastError = true)]
    internal static unsafe partial int MemoryAdvise(byte* address, nuint length, int advice);
}
