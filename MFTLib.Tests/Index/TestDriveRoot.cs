namespace MFTLib.Tests.Index;

/// <summary>
///     A root directory path shaped like the host's, for a synthetic block that has no real
///     directory behind it. Synthetic tests used to be able to leave DriveBlock.RootDirectoryPath
///     null and read a drive-letter path back; MFTLib#143 made the root the only thing a path is
///     built from, so a block that renders paths needs one on both platforms.
/// </summary>
internal static class TestDriveRoot
{
    /// <summary>Returns a host-shaped root directory for the given synthetic drive key.</summary>
    public static string For(char driveLetter)
    {
        return OperatingSystem.IsWindows()
            ? $"{char.ToUpperInvariant(driveLetter)}:\\"
            : $"/mnt/{char.ToLowerInvariant(driveLetter)}";
    }
}
