using System.Threading.Channels;

namespace MFTLib;

/// <summary>Tracks parsing and transfer counts for the elevated broker's per-drive progress reports.</summary>
sealed partial class JournalBrokerHost
{
    // What a finished block scan hands to its completion frames.
    readonly record struct ScanOutput(UsnJournalCursor Cursor, BlockWriteResult WriteResult,
        long MaximumRecordsProcessed, long? TotalRecords);

    sealed class ScanProgressState
    {
        readonly Lock _progressLock = new();
        long _maximumParsingRecordsProcessed;
        long _maximumTransferringRecordsProcessed;
        long? _totalRecordsKnown;

        public void Report(BlockWriteProgress progress, ChannelWriter<BrokerScanProgress> writer)
        {
            lock (_progressLock)
            {
                var (recordsProcessed, totalRecords) = UpdateCounts(progress);
                writer.TryWrite(new BrokerScanProgress
                {
                    Phase = progress.Phase == BlockScanPhase.Parsing ? BrokerScanPhase.Parsing : BrokerScanPhase.Transferring,
                    RecordsProcessed = recordsProcessed,
                    BytesProcessed = progress.BytesProcessed,
                    TotalRecords = totalRecords,
                    TotalBytes = progress.TotalBytes
                });
            }
        }

        public ScanOutput Complete(UsnJournalCursor cursor, BlockWriteResult writeResult)
        {
            lock (_progressLock)
            {
                return new ScanOutput(cursor, writeResult, _maximumParsingRecordsProcessed,
                    _totalRecordsKnown);
            }
        }

        (long RecordsProcessed, long? TotalRecords) UpdateCounts(BlockWriteProgress progress)
        {
            switch (progress.Phase)
            {
                case BlockScanPhase.Parsing:
                    // The parser knows the whole-volume total; a later transfer total counts
                    // only live records and must not replace it with that smaller number.
                    if (progress.TotalRecords.HasValue &&
                        (!_totalRecordsKnown.HasValue || progress.TotalRecords.Value > _totalRecordsKnown.Value))
                    {
                        _totalRecordsKnown = progress.TotalRecords.Value;
                    }

                    _maximumParsingRecordsProcessed = Math.Max(_maximumParsingRecordsProcessed, progress.RecordsProcessed);
                    return (_maximumParsingRecordsProcessed, _totalRecordsKnown);

                case BlockScanPhase.Transferring:
                default:
                    _maximumTransferringRecordsProcessed = Math.Max(_maximumTransferringRecordsProcessed, progress.RecordsProcessed);
                    return (_maximumTransferringRecordsProcessed, progress.TotalRecords);
            }
        }
    }
}
