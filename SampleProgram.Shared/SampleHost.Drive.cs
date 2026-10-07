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

    // Where the index keeps its cache folder; null selects the library's default location. A NoCache open still resolves one.
    internal string? _cacheDirectory;

    static IndexedDrive ResolveDriveNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? IndexedDrive.FromWindowsVolume(letter)
            : throw new PlatformNotSupportedException("Volume serials are read on Windows only.");
    }
}
