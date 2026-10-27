using MFTLib.Index;

namespace MFTLib;

/// <summary>Factories for the <see cref="MftIndexSource" /> an index scans its MFT-backed volumes through.</summary>
public static partial class MftIndexSources
{
    /// <summary>
    ///     A source that scans each volume's live volume in this process, for a caller that is already
    ///     elevated: no broker process is launched. Nothing is opened until the first scan. The source
    ///     offers no watch: <see cref="FileIndex.StartWatchingAsync" /> and
    ///     <see cref="FileIndex.WaitForCatchUpAsync" /> return a per-volume
    ///     <see cref="RootOperationOutcome.NotApplicable" /> result with no failure, and a watch start
    ///     records no watch request. Its volumes report <see cref="RootWatchStatus.IsSupported" /> false.
    ///     A scan from a process that cannot open the volume fails the volume as
    ///     <see cref="RootFailureKind.ProducerFailed" />.
    ///     Cached scans arm the live journal cursor before reading records; reopening adopts only
    ///     an unmoved journal, or one that cannot answer coherently; otherwise the volume rescans,
    ///     and a cache-only open keeps the snapshot flagged unresumable. Cached scans
    ///     require an active journal. A <see cref="MFTLib.Index.CacheMode.Disabled" /> open skips the cursor query and stamps zero.
    /// </summary>
    /// <param name="scanOptions">Freed-record inclusion; null scans every in-use record.</param>
    /// <returns>The source to assign to <see cref="FileIndexOptions.MftSource" />.</returns>
    public static MftIndexSource FromLocalVolumes(MftScanOptions? scanOptions = null)
    {
        return new LocalMftBlockProducer(TimeProvider.System, scanOptions).CreateIndexSource();
    }

}
