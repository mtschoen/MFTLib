using MFTLib.Index;

namespace MFTLib.NativeInterop;

static class MFTUtilities
{
    public static string GetVolumePath(VolumeKey volume) => @$"\\.\{volume.Letter}:";
}
