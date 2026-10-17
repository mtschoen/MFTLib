using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Scans a live volume's MFT in this process into the block each index request names. It carries
///     no broker process, pipe or journal session: the caller must already be able to open the volume.
///     Cached blocks carry the journal cursor armed before scanning; uncached blocks carry zero.
///     Concurrent scans divide the process's parse threads through a shared <see cref="ParseThreadAllocator" />.
/// </summary>
internal sealed class LocalMftBlockProducer(TimeProvider timeProvider, MftScanOptions? scanOptions = null,
    LocalMftBlockProducer.Seams? seams = null, ParseThreadAllocator? allocator = null)
{
    readonly Seams _seams = seams ?? Seams.Live;
    readonly ParseThreadAllocator _allocator = allocator ?? MftBlockProducerPipeline.SharedAllocator;

    /// <summary>The native calls a scan uses; tests replace them.</summary>
    /// <param name="QueryVolumeInformation">Reads the volume geometry that sizes the block.</param>
    /// <param name="ScanDriveRecordBatches">Streams the records of a drive, the in-use ones unless the scan options ask for freed records too.</param>
    /// <param name="QueryCursor">Arms the live journal cursor before a cached scan.</param>
    internal readonly record struct Seams(
        Func<string, NtfsVolumeInformation> QueryVolumeInformation,
        MftRecordBatchSource ScanDriveRecordBatches,
        UsnJournalCursorQuery QueryCursor)
    {
        internal static Seams Live { get; } = new(
            LiveVolumeSources.QueryVolumeInfo, LiveVolumeSources.ScanDriveRecordBatches,
            LiveVolumeSources.QueryCursor);
    }

    /// <summary>The index source over this producer: scans fill the requested block directly, and no watch is offered.</summary>
    internal MftIndexSource CreateIndexSource() => new(ProduceAsync);

    Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken) =>
        MftBlockProducerPipeline.RunAsync(request, _allocator,
            (allowance, token) => Produce(request, allowance, token), cancellationToken);

    MftBlockProduceResult Produce(MftBlockProduceRequest request, ParseThreadAllowance allowance,
        CancellationToken cancellationToken)
    {
        var options = scanOptions ?? new MftScanOptions();
        var drive = request.Drive.ToString();
        var volume = _seams.QueryVolumeInformation(drive);
        var cursor = request.DeleteOnClose ? default : _seams.QueryCursor(drive);
        var progress = MftBlockProgressAdapter.Create(request);
        var plan = new MftBlockProducerPipeline.WritePlan(volume, new BlockStamp(cursor, timeProvider), options);
        // No host watchdog watches a direct scan, so there is no operation to tell what the source is doing.
        return MftBlockProducerPipeline.Write(request, plan,
            (_, recordsOmitted) => _seams.ScanDriveRecordBatches(drive, allowance,
                new BlockWriteReporting(progress, null), options, recordsOmitted, cancellationToken),
            progress, cancellationToken);
    }
}
