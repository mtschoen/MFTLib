using MFTLib.Index;

namespace MFTLib.NativeInterop;

static class MFTUtilities
{
    public static string GetVolumePath(DriveKey drive) => @$"\\.\{drive.Letter}:";
}
