using System.Diagnostics;
using System.Threading.Channels;

namespace MFTLib;

/// <summary>Tracks parsing and transfer counts for the elevated broker's per-drive progress reports.</summary>
public sealed partial class JournalBrokerHost
{
    // What a finished block scan hands to its completion frames.
    readonly record struct ScanOutput(UsnJournalCursor Cursor, BlockWriteResult WriteResult, TimeSpan Elapsed,
        long MaximumRecordsProcessed, long? TotalRecords);

    sealed class ScanProgressState
    {
        readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        readonly object _progressLock = new();
        long _maximumParsingRecordsProcessed;
        long _maximumTransferringRecordsProcessed;
        long? _totalRecordsKnown;

        public void Report(string driveLetter, BlockWriteProgress progress, ChannelWriter<BrokerScanProgress> writer)
        {
            lock (_progressLock)
            {
                var (recordsProcessed, totalRecords) = UpdateCounts(progress);
                writer.TryWrite(new BrokerScanProgress
                {
                    DriveLetter = driveLetter,
                    Phase = progress.Phase,
                    RecordsProcessed = recordsProcessed,
                    BytesProcessed = progress.BytesProcessed,
                    TotalRecords = totalRecords,
                    TotalBytes = progress.TotalBytes,
                    Elapsed = _stopwatch.Elapsed
                });
            }
        }

        public ScanOutput Complete(UsnJournalCursor cursor, BlockWriteResult writeResult)
        {
            lock (_progressLock)
            {
                _stopwatch.Stop();
                return new ScanOutput(cursor, writeResult, _stopwatch.Elapsed, _maximumParsingRecordsProcessed,
                    _totalRecordsKnown);
            }
        }

        (long RecordsProcessed, long? TotalRecords) UpdateCounts(BlockWriteProgress progress)
        {
            switch (progress.Phase)
            {
                case BrokerScanPhase.Parsing:
                    // The parser knows the whole-volume total; a later transfer total counts
                    // only live records and must not replace it with that smaller number.
                    if (progress.TotalRecords.HasValue &&
                        (!_totalRecordsKnown.HasValue || progress.TotalRecords.Value > _totalRecordsKnown.Value))
                    {
                        _totalRecordsKnown = progress.TotalRecords.Value;
                    }

                    _maximumParsingRecordsProcessed = Math.Max(_maximumParsingRecordsProcessed, progress.RecordsProcessed);
                    return (_maximumParsingRecordsProcessed, _totalRecordsKnown);

                case BrokerScanPhase.Transferring:
                default:
                    _maximumTransferringRecordsProcessed = Math.Max(_maximumTransferringRecordsProcessed, progress.RecordsProcessed);
                    return (_maximumTransferringRecordsProcessed, progress.TotalRecords);
            }
        }
    }
}
