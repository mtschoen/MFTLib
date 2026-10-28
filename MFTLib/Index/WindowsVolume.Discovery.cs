using System.Runtime.Versioning;

namespace MFTLib.Index;

public sealed partial record WindowsVolume
{
    /// <summary>
    ///     Reports every volume root with an <see cref="IsIndexable" /> flag and, when false, a display reason.
    ///     A volume is indexable when <c>ProducerSelection.Automatic</c> would scan its MFT: a ready, local
    ///     (fixed or removable) NTFS volume, the format matching ordinally and ignoring case. A root that vanishes or
    ///     refuses access mid-read is reported as not ready.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<WindowsVolume> EnumerateAll() =>
        ReadVolumeVerdicts(DriveInfo.GetDrives(), drive => drive.Name,
            drive => drive.DriveType, drive => drive.IsReady, drive => drive.DriveFormat);

    internal static IReadOnlyList<WindowsVolume> ReadVolumeVerdicts<T>(
        IEnumerable<T> candidates, Func<T, string> rootDirectory, Func<T, DriveType> driveType,
        Func<T, bool> isReady, Func<T, string> driveFormat)
    {
        WindowsVolume Classify(T candidate)
        {
            var root = rootDirectory(candidate);
            var key = new VolumeKey(root[0]);
            var type = DriveType.Unknown;
            try
            {
                type = driveType(candidate);
                if (type is not (DriveType.Fixed or DriveType.Removable))
                {
                    return Verdict(key, root, type, ready: false, format: null);
                }

                if (!isReady(candidate))
                {
                    return Verdict(key, root, type, ready: false, format: null);
                }

                return Verdict(key, root, type, ready: true, format: driveFormat(candidate));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or System.Security.SecurityException)
            {
                return Verdict(key, root, type, ready: false, format: null);
            }
        }

        return [.. candidates.Select(Classify)];
    }

    static WindowsVolume Verdict(VolumeKey key, string root, DriveType type, bool ready, string? format)
    {
        // Readiness is read for fixed and removable roots only; the facts report every other type as ready so the
        // drive type decides, exactly as the open's own facts do.
        var facts = new VolumeFacts(true, key, root, 0, ready || type is not (DriveType.Fixed or DriveType.Removable),
            type, format);
        var unavailable = MftEligibility.Evaluate(in facts, hasMftSource: true);
        return new WindowsVolume(key, root, type)
        {
            IsReady = ready,
            Format = format,
            IsIndexable = unavailable is null,
            Reason = unavailable is null ? null : DescribeReason(type, ready, format)
        };
    }

    static string DescribeReason(DriveType type, bool ready, string? format) => type switch
    {
        DriveType.Network => "network",
        DriveType.CDRom => "optical",
        DriveType.NoRootDirectory => "not ready",
        DriveType.Fixed or DriveType.Removable => ready ? $"not NTFS ({format})" : "not ready",
        DriveType.Unknown => "not ready",
        _ => "other drive type"
    };
}
