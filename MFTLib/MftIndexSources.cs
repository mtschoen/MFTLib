using MFTLib.Index;

namespace MFTLib;

/// <summary>Factories for the <see cref="MftIndexSource" /> an index scans its MFT-backed drives through.</summary>
public static partial class MftIndexSources
{
    /// <summary>
    ///     A source that scans each drive's live volume in this process, for a caller that is already
    ///     elevated: no broker process is launched. Nothing is opened until the first scan. The source
    ///     offers no watch: <see cref="FileIndex.StartWatchingAsync" /> and
    ///     <see cref="FileIndex.WaitForCatchUpAsync" /> return a per-drive
    ///     <see cref="DriveOperationOutcome.NotApplicable" /> result with no failure, and a watch start
    ///     records no watch request. Its drives report <see cref="DriveWatchStatus.IsSupported" /> false.
    ///     A scan from a process that cannot open the volume fails the drive as
    ///     <see cref="DriveFailureKind.ProducerFailed" />.
    ///     Cached scans arm the live journal cursor before reading records; reopening adopts only
    ///     an unmoved journal, or one that cannot answer coherently; otherwise the drive rescans,
    ///     and a cache-only open keeps the snapshot flagged unresumable. Cached scans
    ///     require an active journal. A <see cref="MFTLib.Index.CacheMode.Disabled" /> open skips the cursor query and stamps zero.
    /// </summary>
    /// <param name="scanOptions">Retention and freed-record inclusion; null scans every in-use record.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    public static MftIndexSource FromLocalVolumes(MftScanOptions? scanOptions = null)
    {
        return new LocalMftBlockProducer(TimeProvider.System, scanOptions).CreateIndexSource();
    }

    /// <summary>
    ///     A source that scans nothing and watches nothing. Every scan of a drive fails with
    ///     <see cref="InvalidOperationException" /> whose message is <c>"Drive {letter}: {reason}."</c>,
    ///     which the index reports as <see cref="DriveFailureKind.ProducerFailed" /> with that message
    ///     in <see cref="DriveFailure.Message" />. <see cref="FileIndex.StartWatchingAsync" /> returns
    ///     a per-drive <see cref="DriveOperationOutcome.NotApplicable" /> result with no failure and
    ///     records no watch request. Cached blocks still open.
    /// </summary>
    /// <param name="reason">Why this process cannot scan, written so that it ends a sentence.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is null or white space.</exception>
    public static MftIndexSource Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return MftIndexSource.CreateUnavailable(reason);
    }
}
