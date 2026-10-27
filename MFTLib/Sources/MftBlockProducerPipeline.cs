using MFTLib.Index;

namespace MFTLib;

static class MftBlockProducerPipeline
{
    internal static readonly ParseThreadAllocator SharedAllocator = new(Environment.ProcessorCount);

    internal readonly record struct WritePlan(
        NtfsVolumeInformation Volume, BlockStamp Stamp, MftRowSelection Selection);

    internal static async Task<MftBlockProduceResult> RunAsync(MftBlockProduceRequest request,
        ParseThreadAllocator allocator,
        Func<ParseThreadAllowance, CancellationToken, MftBlockProduceResult> produce,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<MftBlockProduceResult>(cancellationToken).ConfigureAwait(false);
        }
        using var registration = await allocator.AdmitAsync(cancellationToken).ConfigureAwait(false);
        var allowance = registration.Allowance;
        return await Task.Run(() => produce(allowance, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static MftBlockProduceResult Write(MftBlockProduceRequest request,
        WritePlan plan,
        Func<BlockFile, Action<long>, IEnumerable<IReadOnlyList<MftRecord>>> batches,
        IProgress<BlockWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var block = BlockFile.Create(MftBlockCapacity.CreateOptions(plan.Volume, request.Volume,
            request.BlockPath, request.VolumeSerial, request.DeleteOnClose,
            new BlockContentIdentity(request.CacheTag, request.ScanShape)));
        try
        {
            long recordsOmitted = 0;
            var written = MftBlockScan.WriteToBlock(block, plan.Stamp,
                batches(block, count => recordsOmitted += count), plan.Selection,
                new BlockWriteReporting(progress, null), cancellationToken);
            // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to Finish, which disposes it on validation failure or returns it
            var completed = block;
            block = null;
            return MftBlockValidation.Finish(request, completed, plan.Stamp.Cursor,
                MftBlockScan.SaturateSkippedRecordCount(written.SkippedRecordCount + recordsOmitted), null);
        }
        finally
        {
            block?.Dispose();
        }
    }
}
