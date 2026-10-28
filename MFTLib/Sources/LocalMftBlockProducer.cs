using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Scans a live volume's MFT in this process into the block each index request names. It carries
///     no broker process, pipe or journal session: the caller must already be able to open the volume.
///     Cached blocks carry the journal cursor armed before scanning; uncached blocks carry zero.
///     Concurrent scans divide the process's parse threads through a shared <see cref="ParseThreadAllocator" />.
/// </summary>
sealed class LocalMftBlockProducer(TimeProvider timeProvider, MftScanOptions? scanOptions = null,
    IVolumeAccess? volumeAccess = null, ParseThreadAllocator? allocator = null)
{
    readonly IVolumeAccess _volumeAccess = volumeAccess ?? LiveVolumeAccess.Instance;
    readonly ParseThreadAllocator _allocator = allocator ?? MftBlockProducerPipeline.SharedAllocator;


    /// <summary>The index source over this producer: scans fill the requested block directly, and no watch is offered.</summary>
    internal MftIndexSource CreateIndexSource() => new(ProduceAsync, includeFreed: scanOptions?.IncludeFreed ?? false);

    Task<MftBlockProduceResult> ProduceAsync(MftBlockProduceRequest request, CancellationToken cancellationToken) =>
        MftBlockProducerPipeline.RunAsync(request, _allocator,
            (allowance, token) => Produce(request, allowance, token), cancellationToken);

    MftBlockProduceResult Produce(MftBlockProduceRequest request, ParseThreadAllowance allowance,
        CancellationToken cancellationToken)
    {
        var options = scanOptions ?? new MftScanOptions();
        var volume = request.Volume;
        var information = _volumeAccess.QueryVolumeInformation(volume);
        var cursor = request.DeleteOnClose ? default : _volumeAccess.QueryCursor(volume);
        var progress = MftBlockProgressAdapter.Create(request);
        var plan = new MftBlockProducerPipeline.WritePlan(information, new BlockStamp(cursor, timeProvider), options);
        // No host watchdog watches a direct scan, so there is no operation to tell what the source is doing.
        return MftBlockProducerPipeline.Write(request, plan,
            (_, recordsOmitted) => _volumeAccess.ScanVolumeRecordBatches(volume, allowance,
                new BlockWriteReporting(progress, null), options, recordsOmitted, cancellationToken),
            progress, cancellationToken);
    }
}
