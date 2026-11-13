using MFTLib.Index;

namespace MFTLib;

static class MftBlockProgressAdapter
{
    internal static DirectProgress<BlockWriteProgress>? Create(MftBlockProduceRequest request)
    {
        return request.Progress is { } destination
            ? new DirectProgress<BlockWriteProgress>(sample => destination.Report(sample.Phase == BlockScanPhase.Scanning
                ? new IndexScanProgress(request.Volume, IndexScanPhase.Scanning, ClampRows(sample.RecordsProcessed))
                {
                    ExpectedRowCount = sample.TotalRecords is { } total ? ClampRows(total) : null
                }
                : new IndexScanProgress(request.Volume, IndexScanPhase.Indexing, 0)))
            : null;
    }

    internal static DirectProgress<MftScanProgress>? CreateParse(
        IProgress<BlockWriteProgress>? destination, ISourceOperationReporter? operation)
    {
        if (destination is null && operation is null)
        {
            return null;
        }
        return new DirectProgress<MftScanProgress>(sample =>
        {
            operation?.Processing("MFT parse");
            destination?.Report(BlockWriteProgress.Scanning(sample.RecordsScanned, sample.TotalRecords));
        });
    }

    static uint ClampRows(long count) => (uint)Math.Clamp(count, 0L, uint.MaxValue);
}
