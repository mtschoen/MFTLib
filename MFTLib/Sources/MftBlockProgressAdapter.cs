using MFTLib.Index;

namespace MFTLib;

internal static class MftBlockProgressAdapter
{
    internal static DirectProgress<BlockWriteProgress>? Create(MftBlockProduceRequest request)
    {
        return request.Progress is { } destination
            ? new DirectProgress<BlockWriteProgress>(sample => destination.Report(
                new IndexScanProgress(request.Drive,
                    sample.Phase == BlockScanPhase.Parsing
                        ? IndexScanPhase.ParsingMft : IndexScanPhase.Transferring,
                    ClampRows(sample.RecordsProcessed))
                {
                    ExpectedRowCount = sample.TotalRecords is { } total ? ClampRows(total) : null
                }))
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
            destination?.Report(new BlockWriteProgress(sample.RecordsScanned, 0,
                sample.TotalRecords, null, BlockScanPhase.Parsing));
        });
    }

    static uint ClampRows(long count) => (uint)Math.Clamp(count, 0L, uint.MaxValue);
}
