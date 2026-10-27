using MFTLib.Index;

namespace MFTLib.NativeInterop;

static class MFTUtilities
{
    public static string GetVolumePath(VolumeKey drive) => @$"\\.\{drive.Letter}:";
}
