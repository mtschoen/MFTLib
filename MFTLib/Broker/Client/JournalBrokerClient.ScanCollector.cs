using MFTLib.Index;

namespace MFTLib;

/// <summary>Collects per-drive cursors, progress, block outcomes, and errors from the broker's scan frames.</summary>
public sealed partial class JournalBrokerClient
{
    sealed partial class ScanCollector(
        IReadOnlyDictionary<string, string> sectionNamesByDrive,
        IEnumerable<string> drives,
        BrokerScanOptions? options,
        Func<string, IDisposable?> takeSectionLifetime)
    {
        readonly Dictionary<string, UsnJournalCursor> _advancedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalCursor> _armedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalEntry[]> _catchUpEntries = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
        readonly IProgress<BrokerScanProgress>? _progress = options?.Progress;
        readonly HashSet<string> _remaining = new(drives, StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _warnings = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, BlockScanOutcome> _blockOutcomes = new(StringComparer.OrdinalIgnoreCase);

        public required Func<string, BlockFile?> TakePendingBlock { get; init; }

        public bool IsComplete => _remaining.Count == 0;
    }

    sealed partial class ScanCollector
    {
        public void Apply(BrokerFrame frame)
        {
            switch (frame.Kind)
            {
                case BrokerFrameKind.ScanProgress:
                    if (frame.Progress is { } progress)
                    {
                        _progress?.Report(progress);
                    }

                    break;

                case BrokerFrameKind.Cursor:
                    _armedCursors[frame.RequireDrive()] = frame.Cursor;
                    break;

                case BrokerFrameKind.ScanReady:
                    CollectScanReady(frame);
                    break;

                case BrokerFrameKind.JournalBatch:
                    {
                        var drive = frame.RequireDrive();
                        _advancedCursors[drive] = frame.Cursor;
                        _catchUpEntries[drive] = frame.Entries;
                        _remaining.Remove(drive);
                        break;
                    }

                case BrokerFrameKind.Error:
                    {
                        var drive = frame.RequireDrive();
                        _errors[drive] = frame.RequireMessage();
                        if (sectionNamesByDrive.TryGetValue(drive, out var sectionName))
                        {
                            takeSectionLifetime(sectionName)?.Dispose();
                            TakePendingBlock(sectionName)?.Dispose();
                            if (_blockOutcomes.Remove(drive, out var outcome))
                            {
                                outcome.Block.Dispose();
                            }
                        }

                        _remaining.Remove(drive);
                        break;
                    }

                // Non-fatal: the drive still completes via its subsequent JournalBatch
                // frame, so this neither removes it from _remaining nor disposes its section
                // lifetime (that happens on the ScanReady/Error paths above).
                case BrokerFrameKind.Warning:
                    _warnings[frame.RequireDrive()] = frame.RequireMessage();
                    break;
            }
        }

        public BrokerScanResult ToResult()
        {
            return new BrokerScanResult(_armedCursors, _advancedCursors, _catchUpEntries, _errors, _warnings, _blockOutcomes);
        }

        void CollectScanReady(BrokerFrame frame)
        {
            var sectionName = frame.RequireMmfName();
            var matchedDrive = sectionNamesByDrive
                .FirstOrDefault(pair => string.Equals(pair.Value, sectionName, StringComparison.Ordinal)).Key;
            if (matchedDrive == null)
            {
                return;
            }

            try
            {
                CollectBlock(frame, matchedDrive, sectionName);
            }
            finally
            {
                takeSectionLifetime(sectionName)?.Dispose();
            }
        }
    }
}
