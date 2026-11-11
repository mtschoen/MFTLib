using System.ComponentModel;
using MFTLib.Index;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     One volume's USN change journal sizing, as reported by
///     <c>FSCTL_QUERY_USN_JOURNAL</c>. The journal is a ring buffer: when producers write
///     records faster than watchers drain them and the buffer wraps, the overwritten
///     records are gone and every consumer that missed them must rescan.
/// </summary>
public readonly record struct UsnJournalSettings
{
    /// <summary>The journal's configured maximum size in bytes, used by Windows as a trimming target.</summary>
    public required long MaximumSize { get; init; }

    /// <summary>
    ///     The journal's configured allocation delta in bytes.
    /// </summary>
    public required long AllocationDelta { get; init; }

    /// <summary>
    ///     Queries the largest <see cref="MaximumSize" /> in bytes that MFTLib recommends by default
    ///     for <paramref name="volume" />: the smaller of 8 GiB and 5 percent of the volume's total size,
    ///     rounded down to a multiple of 1 MiB, read from Windows at request time without requiring
    ///     administrator rights.
    /// </summary>
    /// <param name="volume">The NTFS volume to query.</param>
    /// <returns>The maximum recommended journal size in bytes before clamping.</returns>
    /// <exception cref="ArgumentException"><paramref name="volume" /> is the default (uninitialized) value.</exception>
    /// <exception cref="Win32Exception">Windows refused the volume disk-space query.</exception>
    /// <exception cref="InvalidOperationException">Windows reported unusable volume geometry or the byte count overflowed.</exception>
    public static long QueryMaximumRecommendedSize(VolumeKey volume) =>
        UsnJournalVolume.QueryMaximumRecommendedSize(MFTUtilities.GetVolumePath(volume.Validated()));
}
