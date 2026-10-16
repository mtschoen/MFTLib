using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial record IndexedDrive
{
    /// <summary>
    ///     Reports every drive root with an <c>IsIndexable</c> flag and, when false, a display reason.
    ///     Only ready fixed NTFS drives are indexable (the format matches ordinally, ignoring case).
    ///     A drive that vanishes or refuses access mid-read is reported as not ready.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<(string RootDirectory, bool IsIndexable, string? Reason)>
        EnumerateWindowsVolumes() =>
        ReadVolumeVerdicts(DriveInfo.GetDrives(), drive => drive.Name,
            drive => drive.DriveType, drive => drive.IsReady, drive => drive.DriveFormat);

    internal static IReadOnlyList<(string RootDirectory, bool IsIndexable, string? Reason)>
        ReadVolumeVerdicts<T>(IEnumerable<T> drives, Func<T, string> rootDirectory,
            Func<T, DriveType> driveType, Func<T, bool> isReady, Func<T, string> driveFormat)
    {
        string? Classify(T drive)
        {
            try
            {
                switch (driveType(drive))
                {
                    case DriveType.Fixed:
                        break;
                    case DriveType.Network:
                        return "network";
                    case DriveType.Removable:
                        return "removable";
                    case DriveType.CDRom:
                        return "optical";
                    case DriveType.NoRootDirectory:
                        return "not ready";
                    default:
                        return "other drive type";
                }
                if (!isReady(drive))
                {
                    return "not ready";
                }
                var format = driveFormat(drive);
                return string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"not NTFS ({format})";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                return "not ready";
            }
        }

        return [.. drives.Select(drive =>
        {
            var reason = Classify(drive);
            return (rootDirectory(drive), reason is null, reason);
        })];
    }
}
