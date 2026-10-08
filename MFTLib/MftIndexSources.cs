using MFTLib.Index;

namespace MFTLib;

/// <summary>Factories for the <see cref="MftIndexSource" /> an index scans its MFT-backed drives through.</summary>
public static partial class MftIndexSources
{
    /// <summary>
    ///     A source that scans each drive's live volume in this process, for a caller that is already
    ///     elevated: no broker process is launched. Nothing is opened until the first scan. The source
    ///     offers no watch, so a watch start or a per-drive catch-up is refused with
    ///     <c>Drive {letter}: this source does not support watching.</c> and its drives report
    ///     <see cref="DriveWatchStatus.Supported" /> false. A scan from a process that cannot open
    ///     the volume fails the drive as <see cref="DriveFailureKind.ProducerFailed" />.
    ///     Cached scans arm the live journal cursor before reading records; reopening adopts only
    ///     an unmoved journal, or one that cannot answer coherently; otherwise the drive rescans,
    ///     and a cache-only open keeps the snapshot flagged unresumable. Cached scans
    ///     require an active journal. NoCache skips the cursor query and stamps zero.
    /// </summary>
    /// <param name="scanOptions">The scan profile and keep-file names; null scans every record.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    public static MftIndexSource FromLocalVolumes(BrokerScanOptions? scanOptions = null)
    {
        return new LocalMftBlockProducer(scanOptions).CreateIndexSource();
    }
}
