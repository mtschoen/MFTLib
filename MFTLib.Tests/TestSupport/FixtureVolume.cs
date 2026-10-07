namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Freed rows come only from the volume export, so a test parses a fixture MFT with freed rows by
///     serving it as a synthetic NTFS image. The image replaces record 0 with its own $MFT record;
///     every other record keeps its bytes. Windows only: see <see cref="WindowsOnlyNative" />. The
///     caller resets the native test hooks, since the parse sets the volume record size override.
/// </summary>
internal static class FixtureVolume
{
    internal static MftResult Parse(string imagePath, byte[] mft, bool includeFreed)
    {
        SyntheticNtfsImage.Write(imagePath, mft);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        using var image = File.OpenHandle(imagePath);
        return new MftResult(MFTLibNative._parseMftRecordsWithProgress(image, includeFreed, 64, IntPtr.Zero, null));
    }
}
