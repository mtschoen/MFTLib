using MFTLib.Index;

namespace MFTLib;

static class MftBlockProducerPipeline
{
    internal static readonly ParseThreadAllocator SharedAllocator = new(Environment.ProcessorCount);

    internal readonly record struct WritePlan(
        NtfsVolumeInformation Volume, BlockStamp Stamp, MftScanOptions ScanOptions);

    internal static async Task<MftBlockProduceResult> RunAsync(MftBlockProduceRequest request,
        ParseThreadAllocator allocator,
        Func<MftBlockProduceRequest, ParseThreadAllowance, CancellationToken, MftBlockProduceResult> produce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<MftBlockProduceResult>(cancellationToken).ConfigureAwait(false);
        }
        using var registration = await allocator.AdmitAsync(cancellationToken).ConfigureAwait(false);
        var allowance = registration.Allowance;
        return await Task.Run(
                () => MftBlockGrowth.ProduceUntilFit(request, grown => produce(grown, allowance, cancellationToken)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Creates the block, then opens the source and writes its rows while it parses. A block that exists when the
    ///     scan fails is that scan's scratch space: it is disposed, and a delete-on-close block deletes itself.
    /// </summary>
    internal static MftBlockProduceResult Write(MftBlockProduceRequest request,
        WritePlan plan,
        Func<IMftSliceSource> openSource,
        IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var location = new MftBlockCapacity.FileLocation(request.Volume, request.BlockPath, request.VolumeSerial,
            request.DeleteOnClose);
        var block = BlockFile.Create(MftBlockCapacity.CreateOptions(plan.Volume, location,
            new BlockContentIdentity(request.CacheTag, request.ScanShape), request.MinimumCapacities));
        try
        {
            BlockWriteResult written;
            using (var source = openSource())
            {
                written = MftBlockScan.WriteToBlock(block, plan.Stamp, source, plan.ScanOptions,
                    new BlockWriteReporting(progress, null), cancellationToken);
            }

            // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to Finish, which disposes it on validation failure or returns it
            var completed = block;
            block = null;
            return MftBlockValidation.Finish(request, completed, plan.Stamp.Cursor,
                MftBlockScan.SaturateSkippedRecordCount(written.SkippedRecordCount), null);
        }
        finally
        {
            block?.Dispose();
        }
    }
}
