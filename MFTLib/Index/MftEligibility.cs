namespace MFTLib.Index;

/// <summary>
///     The single definition of MFT eligibility. Producer selection at open and <see cref="WindowsVolume.IsIndexable" />
///     both call it, so a consumer listing drives and an index opening a root can never disagree.
/// </summary>
static class MftEligibility
{
    /// <summary>
    ///     The first fact that rules the MFT out, or null when the volume is eligible. The order is fixed: platform,
    ///     drive letter, readiness, drive type (fixed or removable), file system (NTFS, ignoring case), source.
    /// </summary>
    /// <param name="facts">The volume facts read at open.</param>
    /// <param name="hasMftSource">Whether <see cref="FileIndexOptions.MftSource" /> is present.</param>
    internal static MftUnavailableReason? Evaluate(in VolumeFacts facts, bool hasMftSource)
    {
        if (!facts.PlatformHasMft)
        {
            return MftUnavailableReason.PlatformWithoutMft;
        }

        if (facts.Volume is null)
        {
            return MftUnavailableReason.NotLocalVolume;
        }

        if (!facts.IsReady)
        {
            return MftUnavailableReason.VolumeNotReady;
        }

        if (facts.DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            return MftUnavailableReason.NotLocalVolume;
        }

        if (!string.Equals(facts.Format, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return MftUnavailableReason.NotNtfs;
        }

        return hasMftSource ? null : MftUnavailableReason.NoMftSource;
    }
}
