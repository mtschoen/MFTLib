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
    public static IReadOnlyList<WindowsVolume> EnumerateWindowsVolumes() =>
        ReadVolumeVerdicts(DriveInfo.GetDrives(), drive => drive.Name,
            drive => drive.DriveType, drive => drive.IsReady, drive => drive.DriveFormat);

    internal static IReadOnlyList<WindowsVolume> ReadVolumeVerdicts<T>(
        IEnumerable<T> drives, Func<T, string> rootDirectory, Func<T, DriveType> driveType,
        Func<T, bool> isReady, Func<T, string> driveFormat)
    {
        WindowsVolume Classify(T drive)
        {
            var root = rootDirectory(drive);
            var key = new VolumeKey(root[0]);
            var type = DriveType.Unknown;
            try
            {
                type = driveType(drive);
                string? reason;
                switch (type)
                {
                    case DriveType.Fixed:
                        reason = null;
                        break;
                    case DriveType.Network:
                        reason = "network";
                        break;
                    case DriveType.Removable:
                        reason = "removable";
                        break;
                    case DriveType.CDRom:
                        reason = "optical";
                        break;
                    case DriveType.NoRootDirectory:
                        reason = "not ready";
                        break;
                    default:
                        reason = "other drive type";
                        break;
                }
                if (type != DriveType.Fixed)
                {
                    return new WindowsVolume(key, root, type) { Reason = reason };
                }
                if (!isReady(drive))
                {
                    return new WindowsVolume(key, root, type) { Reason = "not ready" };
                }
                var format = driveFormat(drive);
                var isNtfs = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);
                return new WindowsVolume(key, root, type)
                {
                    IsReady = true,
                    Format = format,
                    IsIndexable = isNtfs,
                    Reason = isNtfs ? null : $"not NTFS ({format})"
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                return new WindowsVolume(key, root, type) { Reason = "not ready" };
            }
        }

        return [.. drives.Select(Classify)];
    }
}
