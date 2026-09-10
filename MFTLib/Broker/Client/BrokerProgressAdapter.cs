using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Forwards broker scan progress to the caller's own broker progress channel, if any, and
///     composes an <see cref="IndexScanProgress" /> sample for the index request's progress
///     channel from each report.
/// </summary>
internal sealed class BrokerProgressAdapter(MftBlockProduceRequest request,
    IProgress<BrokerScanProgress>? scanProgress) : IProgress<BrokerScanProgress>
{
    /// <summary>
    ///     Returns null when neither the index request nor the caller supplied a progress
    ///     channel, so the broker scan skips composing progress samples it has no destination for.
    /// </summary>
    internal static BrokerProgressAdapter? Create(MftBlockProduceRequest request,
        IProgress<BrokerScanProgress>? scanProgress)
    {
        if (request.Progress is null && scanProgress is null)
        {
            return null;
        }

        return new BrokerProgressAdapter(request, scanProgress);
    }

    public void Report(BrokerScanProgress sample)
    {
        scanProgress?.Report(sample);

        if (request.Progress is null)
        {
            return;
        }

        request.Progress.Report(new IndexScanProgress
        {
            DriveLetter = request.DriveLetter,
            Phase = sample.Phase == BrokerScanPhase.Parsing
                ? IndexScanPhase.ParsingMft
                : IndexScanPhase.Transferring,
            RowsWritten = (uint)Math.Min(sample.RecordsProcessed, uint.MaxValue),
            TotalRows = sample.TotalRecords is { } total
                ? (uint)Math.Min(total, uint.MaxValue)
                : null
        });
    }
}
