using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Projects a production's progress onto <see cref="IndexScanProgress" />. A scanning count never exceeds the expected
///     total the same sample carries, whatever the parse reported; <see cref="FileIndex" /> keeps it from decreasing.
/// </summary>
static class MftBlockProgressAdapter
{
    internal static DirectProgress<BlockWriteProgress>? Create(MftBlockProduceRequest request)
    {
        return request.Progress is { } destination
            ? new DirectProgress<BlockWriteProgress>(sample => destination.Report(sample.Phase == BlockScanPhase.Scanning
                ? new IndexScanProgress(request.Volume, IndexScanPhase.Scanning, ClampRows(sample.TotalRecords is { } total
                    ? Math.Min(sample.RecordsProcessed, total)
                    : sample.RecordsProcessed))
                {
                    ExpectedRowCount = sample.TotalRecords is { } expected ? ClampRows(expected) : null
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
