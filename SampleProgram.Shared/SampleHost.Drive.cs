using MFTLib.Index;

#if SAMPLE_WATCH
namespace SampleProgram.Watch;
#else
namespace SampleProgram.Direct;
#endif

partial class SampleHost
{
    static IndexedDrive ResolveDriveNative(string letter)
    {
        return OperatingSystem.IsWindows()
            ? IndexedDrive.FromWindowsDrive(new DriveKey(letter[0]))
            : throw new PlatformNotSupportedException("Volume serials are read on Windows only.");
    }
}
