using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Elevated-side broker logic. One control pipe carries requests; each drive operation (one
///     scan, or one watch) runs on its own pipe, so drives never share a stream, a write lock or
///     a failure. A scan arms the journal cursor BEFORE scanning, so changes made during the scan
///     are replayed by catch-up. Volume access is injected so the core is testable without real
///     elevation; <see cref="CreateDefault" /> wires the real MFTLib seams.
/// </summary>
public sealed partial class JournalBrokerHost
{
    /// <summary>How long a closed control pipe waits for every channel to end before the session returns.</summary>
    internal static readonly TimeSpan ControlClosedGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>How long an <see cref="BrokerFrameKind.OpenChannel" /> request waits for its drive pipe to connect.</summary>
    internal static readonly TimeSpan ChannelConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a connected drive pipe waits for its one request frame.</summary>
    internal static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(30);

    internal static TimeSpan _progressThrottleInterval = TimeSpan.FromMilliseconds(250);

    // The processing step a watch publishes while it ships one journal batch.
    const string JournalBatchStep = "journal batch";

    readonly UsnJournalCursorQuery _queryCursor;
    readonly ScanSources? _scanSources;
    readonly JournalBatchSource? _watchDrive;
    readonly NtfsVolumeInformationQuery? _queryVolumeInfo;
    readonly GrowUsnJournalQuery? _growUsnJournal;
    readonly TimeProvider _timeProvider;
    readonly ParseThreadAllocator _parseThreads;

    /// <summary>Builds a host over injected volume access, so it runs without real elevation in tests.</summary>
    /// <param name="queryCursor">Arms a drive's journal cursor before its scan, and bounds a watch's backlog.</param>
    /// <param name="scanDrive">Streams one drive's MFT records for a scan; null refuses every scan.</param>
    /// <param name="readJournal">Replays the journal from a scan's armed cursor after the scan; null refuses every scan.</param>
    /// <param name="watchDrive">Streams a drive's journal for a watch; null refuses every watch.</param>
    /// <param name="queryVolumeInfo">Answers volume sizing queries; null refuses every query.</param>
    /// <param name="growUsnJournal">Grows a drive's journal; null refuses every grow request.</param>
    /// <param name="processorCount">The parse-thread budget every running scan shares; null is <see cref="Environment.ProcessorCount" />.</param>
    /// <param name="timeProvider">The clock of every host timeout; null is <see cref="TimeProvider.System" />.</param>
    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        MftRecordBatchSource? scanDrive = null,
        UsnJournalCatchUpSource? readJournal = null,
        JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null,
        GrowUsnJournalQuery? growUsnJournal = null,
        int? processorCount = null,
        TimeProvider? timeProvider = null)
    {
        _queryCursor = queryCursor;
        _scanSources = scanDrive != null && readJournal != null ? new ScanSources(scanDrive, readJournal) : null;
        _watchDrive = watchDrive;
        _queryVolumeInfo = queryVolumeInfo;
        _growUsnJournal = growUsnJournal;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _parseThreads = new ParseThreadAllocator(processorCount ?? Environment.ProcessorCount);
    }

    // A scan needs both sources; a host missing either refuses every scan.
    readonly record struct ScanSources(MftRecordBatchSource ScanDrive, UsnJournalCatchUpSource JournalReader);

    // What one scan's block write needs: where the records go and where they come from.
    readonly record struct ScanStage(IBlockSectionWriter Writer, MftRecordBatchSource ScanDrive);

    /// <summary>The parse-thread budget every scan on this host is admitted to.</summary>
    internal ParseThreadAllocator ParseThreads => _parseThreads;

    /// <summary>Test hook, set before serving: receives each drive pipe's tag and new state on every publication.</summary>
    internal Action<string, ChannelOperationState>? OperationStatePublishedForTest { get; set; }

    /// <summary>Test hook, set before serving: runs on the heartbeat sender's thread after each visit.</summary>
    internal Action? HeartbeatVisitedForTest { get; set; }

    // One watch channel: stream this drive's journal from the requested cursor until the
    // channel is cancelled (its pipe closed or the session ended) or the watch fails. The loop
    // publishes WaitingOnVolume before each read, which heartbeats however long the journal stays
    // quiet, and Processing while it ships a batch.
    async Task StreamWatchAsync(DriveChannel channel, UsnJournalCursor since, CancellationToken cancellationToken)
    {
        var drive = channel.Drive;
        var yieldedAny = false;
        try
        {
            if (_watchDrive == null)
            {
                await WriteChannelErrorAsync(channel, "Broker has no watch source", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var arm = await ArmWatchAsync(channel, since, cancellationToken).ConfigureAwait(false);

            // While diagnostics are enabled, drop the diagnostics logs' own journal entries: each
            // shipped frame is logged, the log write produces a USN record, and that record would
            // ship as another frame. A batch the filter leaves empty is skipped entirely - shipping
            // it would keep the loop alive, because even an empty frame gets logged.
            var logFilter = BrokerDiagnostics.CreateLogFilter();
            var caughtUpReported = arm.CaughtUpReported;
            var batches = _watchDrive(drive, arm.Since, channel.Pipe, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            await using var batchesLifetime = batches.ConfigureAwait(false);
            while (true)
            {
                channel.Pipe.WaitingOnVolume();
                if (!await batches.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                channel.Pipe.Processing(JournalBatchStep);
                var (entries, cursor) = batches.Current;
                yieldedAny = true;
                var filtered = logFilter?.Filter(drive, entries) ?? entries;
                if (filtered.Length > 0)
                {
                    await channel.Pipe.WriteFrameAsync(
                        writer => BrokerProtocol.WriteJournalBatch(writer, cursor, filtered),
                        cancellationToken).ConfigureAwait(false);
                }

                if (!caughtUpReported && cursor.JournalId == arm.Tip.JournalId &&
                    cursor.NextUsn >= arm.Tip.NextUsn)
                {
                    await channel.Pipe.WriteFrameAsync(BrokerProtocol.WriteCaughtUp, cancellationToken)
                        .ConfigureAwait(false);
                    caughtUpReported = true;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not ClientDisconnectedException)
        {
            // A failure ends this channel's watch with its Error frame. There is no resume from
            // the current journal position, because a consumer that applied batches from the far
            // side of a lost replay gap would advance past USN records nothing will ever replay and
            // diverge from the volume in silence. A rescan is the only recovery.
            await WriteChannelErrorAsync(channel, DescribeWatchFailure(drive, since, yieldedAny, exception),
                cancellationToken).ConfigureAwait(false);
        }
    }

    // Resolves what bounds this watch's backlog and reports the leading CaughtUp marker when the
    // requested cursor already sits at the journal tip.
    async Task<(UsnJournalCursor Tip, UsnJournalCursor Since, bool CaughtUpReported)> ArmWatchAsync(
        DriveChannel channel, UsnJournalCursor since, CancellationToken cancellationToken)
    {
        // The journal tip at arm time is what bounds this watch's backlog: every record up to it
        // must be delivered before the drive can be called caught up. One query per watch, and the
        // same query resolves a (0,0) sentinel into a watch-from-now cursor, so only the
        // pre-launch gap is lost, and there is no cached cursor that could have gone stale.
        channel.Pipe.WaitingOnVolume();
        var tip = _queryCursor(channel.Drive);
        var effectiveSince = since.JournalId == 0 ? tip : since;

        // No backlog at all: the drive starts on live entries, so the marker leads. The journal id
        // guard runs on both comparisons: two journals' USN offsets are not comparable, and a
        // mismatched tip belongs to a dead journal generation.
        var caughtUpReported = effectiveSince.JournalId == tip.JournalId &&
                               effectiveSince.NextUsn >= tip.NextUsn;
        if (caughtUpReported)
        {
            await channel.Pipe.WriteFrameAsync(BrokerProtocol.WriteCaughtUp, cancellationToken)
                .ConfigureAwait(false);
        }

        return (tip, effectiveSince, caughtUpReported);
    }

    // A failed cached-cursor startup gets rescan wording only when the journal now proves that
    // position is lost. Unknown journal state preserves the original error, as do sentinel starts
    // and failures after batches have flowed.
    static string DescribeWatchFailure(string drive, UsnJournalCursor since, bool yieldedAny, Exception exception)
    {
        if (yieldedAny || since.JournalId == 0 ||
            !BrokerDriveLetter.TryNormalize(drive, out var normalizedDrive) ||
            JournalCheckpointCheck.Check(normalizedDrive[0], since.JournalId, since.NextUsn,
                JournalCheckpointLossDetection.LiveWatch) is null)
        {
            return exception.Message;
        }

        var cursorText = FormattableString.Invariant($"{since.JournalId}:{since.NextUsn}");
        return $"Drive {drive} cannot resume its live watch from journal cursor {cursorText}: " +
               $"{exception.Message}. The records between that cursor and the current journal position " +
               "are gone, so this drive needs a rescan before it can be watched again.";
    }

    sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
