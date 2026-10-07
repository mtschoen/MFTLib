using System.Diagnostics;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Scans a live volume's MFT in this process into the block each index request names. It carries
///     no broker process, pipe or journal session: the caller must already be able to open the volume.
///     The block it completes carries a zero journal cursor, so the source it builds has no watch source.
///     Concurrent scans divide the process's parse threads through a shared <see cref="ParseThreadAllocator" />.
/// </summary>
internal sealed class LocalMftBlockProducer
{
    static readonly ParseThreadAllocator SharedAllocator = new(Environment.ProcessorCount);

    readonly BrokerScanOptions? _scanOptions;
    readonly Seams _seams;
    readonly ParseThreadAllocator _allocator;

    /// <summary>Builds a producer over the live volume seams.</summary>
    /// <param name="scanOptions">Profile, keep-file names and progress for every scan.</param>
    internal LocalMftBlockProducer(BrokerScanOptions? scanOptions = null)
        : this(scanOptions, Seams.Live)
    {
    }

    /// <summary>Builds a producer over the given volume seams and parse-thread allocator.</summary>
    internal LocalMftBlockProducer(BrokerScanOptions? scanOptions, Seams seams,
        ParseThreadAllocator? allocator = null)
    {
        _scanOptions = scanOptions;
        _seams = seams;
        _allocator = allocator ?? SharedAllocator;
    }

    /// <summary>The native calls and clock a scan uses; tests replace them.</summary>
    /// <param name="QueryVolumeInformation">Reads the volume geometry that sizes the block.</param>
    /// <param name="ScanDriveRecordBatches">Streams the in-use records of a drive.</param>
    /// <param name="Clock">Supplies the completion time stamped into the block.</param>
    internal readonly record struct Seams(
        Func<string, NtfsVolumeInformation> QueryVolumeInformation,
        MftRecordBatchSource ScanDriveRecordBatches,
        Func<DateTime> Clock)
    {
        internal static Seams Live { get; } = new(
            LiveVolumeSources.QueryVolumeInfo, LiveVolumeSources.ScanDriveRecordBatches, () => DateTime.UtcNow);
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
        var options = _scanOptions ?? new BrokerScanOptions();
        var drive = request.DriveLetter.ToString();
        BlockFile? block = BlockFile.Create(MftBlockCapacity.CreateOptions(
            _seams.QueryVolumeInformation(drive), request.BlockPath, request.VolumeSerial, request.DeleteOnClose,
            request.CacheTag));
        try
        {
            var progress = CreateProgress(request, options);
            var batches = _seams.ScanDriveRecordBatches(drive, allowance,
                IdleOperation.Instance, progress, cancellationToken);
            var result = MftBlockScan.WriteToBlock(block, new BlockStamp(default, _seams.Clock), batches,
                new MftBlockRowFilter(options.Profile, options.KeepFileNames),
                new BlockWriteReporting(progress, null), cancellationToken);
            BrokerMftBlockProducer.ValidateBlock(block, request.VolumeSerial, default, request.CacheTag);
            var produced = new MftBlockProduceResult(block, 0, 0, checked((int)result.SkippedRecordCount));
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
        BrokerScanOptions options)
    {
        var destination = BrokerProgressAdapter.Create(request, options.Progress);
        if (destination is null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        return new DirectProgress<BlockWriteProgress>(value => destination.Report(new BrokerScanProgress
        {
            DriveLetter = request.DriveLetter.ToString(),
            Phase = value.Phase,
            RecordsProcessed = value.RecordsProcessed,
            BytesProcessed = value.BytesProcessed,
            TotalRecords = value.TotalRecords,
            TotalBytes = value.TotalBytes,
            Elapsed = stopwatch.Elapsed
        }));
    }

    // No host watchdog watches a direct scan, so there is nobody to tell what the source is doing.
    sealed class IdleOperation : IBrokerOperationReporter
    {
        internal static readonly IdleOperation Instance = new();

        public void WaitingOnVolume()
        {
        }

        public void Processing(string stepName)
        {
        }
    }
}
