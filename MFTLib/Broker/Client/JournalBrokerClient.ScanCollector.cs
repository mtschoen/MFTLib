namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    sealed partial class ScanCollector(
        IMmfReader mmfReader,
        IReadOnlyDictionary<string, string> mmfNamesByDrive,
        IEnumerable<string> drives,
        BrokerScanOptions? options,
        Func<string, IDisposable?> takeMmfLifetime,
        CancellationToken cancellationToken)
    {
        readonly Dictionary<string, UsnJournalCursor> _advancedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalCursor> _armedCursors = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UsnJournalEntry[]> _catchUpEntries = new(StringComparer.OrdinalIgnoreCase);
        readonly ScanRecordBatchConsumer _consumeRecords = options?.ConsumeRecords ?? (static (_, _) => ValueTask.CompletedTask);
        readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
        readonly IProgress<BrokerScanProgress>? _progress = options?.Progress;
        readonly HashSet<string> _remaining = new(drives, StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _warnings = new(StringComparer.OrdinalIgnoreCase);

        public bool IsComplete => _remaining.Count == 0;
    }

    sealed partial class ScanCollector
    {
        public async ValueTask ApplyAsync(BrokerFrame frame)
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
                    {
                        var memoryMappedFileName = frame.RequireMmfName();
                        var matchedDrive = mmfNamesByDrive
                            .FirstOrDefault(pair => string.Equals(
                                pair.Value, memoryMappedFileName, StringComparison.Ordinal))
                            .Key;
                        if (matchedDrive != null)
                        {
                            try
                            {
                                if (mmfReader is IStreamingMmfReader streamingReader)
                                {
                                    foreach (var batch in streamingReader.ReadBatches(
                                                 memoryMappedFileName, frame.ByteLength, 4096, cancellationToken))
                                    {
                                        await _consumeRecords(batch, cancellationToken).ConfigureAwait(false);
                                    }
                                }
                                else
                                {
                                    var records = mmfReader.Read(memoryMappedFileName, frame.ByteLength);
                                    if (records.Length > 0)
                                    {
                                        await _consumeRecords(records, cancellationToken).ConfigureAwait(false);
                                    }
                                }
                            }
                            finally
                            {
                                takeMmfLifetime(memoryMappedFileName)?.Dispose();
                            }
                        }

                        break;
                    }

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
                        if (mmfNamesByDrive.TryGetValue(drive, out var memoryMappedFileName))
                        {
                            takeMmfLifetime(memoryMappedFileName)?.Dispose();
                        }

                        _remaining.Remove(drive);
                        break;
                    }

                // Non-fatal: the drive still completes via its subsequent JournalBatch
                // frame, so this neither removes it from _remaining nor disposes its MMF
                // lifetime (that happens on the ScanReady/Error paths above).
                case BrokerFrameKind.Warning:
                    _warnings[frame.RequireDrive()] = frame.RequireMessage();
                    break;
            }
        }

        public BrokerScanResult ToResult()
        {
            return new BrokerScanResult(_armedCursors, _advancedCursors, _catchUpEntries, _errors, _warnings);
        }
    }
}
