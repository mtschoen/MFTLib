using MFTLib.Index;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

// How both samples name a drive for the index: the volume identity, and where the cache folder lives.
partial class SampleHost
{
    internal Func<string, IndexedDrive> _resolveDrive = ResolveDriveNative;

    // An optional sample cache-root override. Watch owns its default; Direct uses NoCache.
    internal string? _cacheDirectory;

    static IndexedDrive ResolveDriveNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? IndexedDrive.FromWindowsVolume(letter)
            : throw new PlatformNotSupportedException("Volume serials are read on Windows only.");
    }
}
