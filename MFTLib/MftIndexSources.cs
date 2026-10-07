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
    ///     <see cref="DriveStatus.WatchSupported" /> false. A scan from a process that cannot open
    ///     the volume fails the drive as <see cref="DriveFailureKind.ProducerFailed" />.
    /// </summary>
    /// <param name="scanOptions">The scan profile and keep-file names; null scans every record.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    public static MftIndexSource FromLocalVolumes(BrokerScanOptions? scanOptions = null)
    {
        return new LocalMftBlockProducer(scanOptions).CreateIndexSource();
    }
}
