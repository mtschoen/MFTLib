using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A live volume scan (<see cref="LiveVolumeSources.ScanVolumeRecordBatches" />) sizes its chunks from
///     FSCTL_GET_NTFS_VOLUME_DATA, which <see cref="LiveVolumeSources.QueryVolumeInfo" /> refuses off Windows
///     with <see cref="PlatformNotSupportedException" />. A test of that pipeline marks itself inconclusive
///     there and returns early; the volume parse it ends in runs on every platform.
/// </summary>
static class WindowsOnlyLiveScan
{
    internal static bool Skip()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Assert.Inconclusive("A live volume scan queries FSCTL_GET_NTFS_VOLUME_DATA, which only Windows answers.");
        return true;
    }
}
