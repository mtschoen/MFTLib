using System.Diagnostics;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Scans a live volume's MFT in this process into the block each index request names. It carries
///     no broker process, pipe or journal session: the caller must already be able to open the volume.
///     Cached blocks carry the journal cursor armed before scanning; uncached blocks carry zero.
///     Concurrent scans divide the process's parse threads through a shared <see cref="ParseThreadAllocator" />.
/// </summary>
internal sealed class LocalMftBlockProducer
{
    internal static readonly ParseThreadAllocator SharedAllocator = new(Environment.ProcessorCount);

    readonly MftScanOptions? _scanOptions;
    readonly Seams _seams;
    readonly ParseThreadAllocator _allocator;

    /// <summary>Builds a producer over the live volume seams.</summary>
    /// <param name="scanOptions">Retention, freed-record inclusion and progress for every scan.</param>
    internal LocalMftBlockProducer(MftScanOptions? scanOptions = null)
        : this(scanOptions, Seams.Live)
    {
    }

    /// <summary>Builds a producer over the given volume seams and parse-thread allocator.</summary>
    internal LocalMftBlockProducer(MftScanOptions? scanOptions, Seams seams,
        ParseThreadAllocator? allocator = null)
    {
        _scanOptions = scanOptions;
        _seams = seams;
        _allocator = allocator ?? SharedAllocator;
    }

    /// <summary>The native calls and clock a scan uses; tests replace them.</summary>
    /// <param name="QueryVolumeInformation">Reads the volume geometry that sizes the block.</param>
    /// <param name="ScanDriveRecordBatches">Streams the records of a drive, the in-use ones unless the scan options ask for freed records too.</param>
    /// <param name="Clock">Supplies the completion time stamped into the block.</param>
    /// <param name="QueryCursor">Arms the live journal cursor before a cached scan.</param>
    internal readonly record struct Seams(
        Func<string, NtfsVolumeInformation> QueryVolumeInformation,
        MftRecordBatchSource ScanDriveRecordBatches,
        Func<DateTime> Clock,
        UsnJournalCursorQuery QueryCursor)
    {
        internal static Seams Live { get; } = new(
            LiveVolumeSources.QueryVolumeInfo, LiveVolumeSources.ScanDriveRecordBatches, () => DateTime.UtcNow,
            LiveVolumeSources.QueryCursor);
    }

    /// <summary>The index source over this producer: scans fill the requested block directly, and no watch is offered.</summary>
    internal MftIndexSource CreateIndexSource() => new(ProduceAsync);

    async Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<MftBlockProduceResult>(cancellationToken).ConfigureAwait(false);
        }

        using var registration = await _allocator.AdmitAsync(cancellationToken).ConfigureAwait(false);
        var allowance = registration.Allowance;
        return await Task.Run(() => Produce(request, allowance, cancellationToken)).ConfigureAwait(false);
    }

    MftBlockProduceResult Produce(MftBlockProduceRequest request, ParseThreadAllowance allowance,
        CancellationToken cancellationToken)
    {
        var options = _scanOptions ?? new MftScanOptions();
        var drive = request.Drive.ToString();
        BlockFile? block = BlockFile.Create(MftBlockCapacity.CreateOptions(
            _seams.QueryVolumeInformation(drive), request.BlockPath, request.VolumeSerial, request.DeleteOnClose,
            request.CacheTag));
        try
        {
            var cursor = request.DeleteOnClose ? default : _seams.QueryCursor(drive);
            var progress = CreateProgress(request, options);
            // No host watchdog watches a direct scan, so there is no operation to tell what the source is doing.
            var reporting = new BlockWriteReporting(progress, null);
            long recordsOmitted = 0;
            var batches = _seams.ScanDriveRecordBatches(drive, allowance, reporting, options,
                count => recordsOmitted += count, cancellationToken);
            var result = MftBlockScan.WriteToBlock(block, new BlockStamp(cursor, _seams.Clock), batches, options,
                reporting, cancellationToken);
            BrokerMftBlockProducer.ValidateBlock(block, request.VolumeSerial, cursor, request.CacheTag);
            var produced = new MftBlockProduceResult(block, cursor.JournalIdentifier, cursor.NextUsn,
                MftBlockScan.SaturateSkippedRecordCount(result.SkippedRecordCount + recordsOmitted));
            block = null;
            return produced;
        }
        finally
        {
            block?.Dispose();
        }
    }

    // Null when neither the index request nor the scan options want progress, so no samples are composed.
    static DirectProgress<BlockWriteProgress>? CreateProgress(MftBlockProduceRequest request,
        MftScanOptions options)
    {
        var destination = BrokerProgressAdapter.Create(request, options.Progress);
        if (destination is null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        return new DirectProgress<BlockWriteProgress>(value => destination.Report(new BrokerScanProgress
        {
            Drive = request.Drive.ToString(),
            Phase = value.Phase,
            RecordsProcessed = value.RecordsProcessed,
            BytesProcessed = value.BytesProcessed,
            TotalRecords = value.TotalRecords,
            TotalBytes = value.TotalBytes,
            Elapsed = stopwatch.Elapsed
        }));
    }
}
