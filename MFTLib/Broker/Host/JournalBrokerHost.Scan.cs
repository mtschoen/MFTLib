using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace MFTLib;

/// <summary>Serves per-drive block scans and journal catch-up while isolating drive failures.</summary>
public sealed partial class JournalBrokerHost
{
    async Task HandleArmAndScanAsync(
        Stream stream,
        IBlockSectionWriter? blockSectionWriter,
        string drivesSpec,
        IReadOnlyList<string> keepFileNames,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        foreach (var request in ParseScanSpec(drivesSpec))
        {
            try
            {
                await ProcessDriveScanAsync(stream, blockSectionWriter, request, keepFileNames, writeLock, cancellationToken)
                    .ConfigureAwait(false);
            }
            // Deliberate per-drive boundary: any failure on one drive (journal
            // wrapped, volume open denied, scan IO error) is reported as an Error
            // frame and the remaining drives still proceed - matching the existing
            // non-fatal per-drive journal contract. A throw here would abort the
            // whole session, losing the other drives' scans. Cancellation is not a
            // per-drive error: let it propagate to end the session cleanly.
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = exception.Message;
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, request.Letter, BrokerFrame.NoArmEpoch, message),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    async Task ProcessDriveScanAsync(
        Stream stream,
        IBlockSectionWriter? blockSectionWriter,
        ScanDriveRequest request,
        IReadOnlyList<string> keepFileNames,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var progressChannel = Channel.CreateBounded<BrokerScanProgress>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        var pumpTask = Task.Run(
            () => RunProgressPumpAsync(stream, progressChannel.Reader, writeLock, cancellationToken),
            CancellationToken.None);

        (UsnJournalCursor cursor, BlockWriteResult writeResult, TimeSpan scanElapsed, long maximumRecordsProcessed, long?
            totalRecords) scanOutput;
        try
        {
            scanOutput = await Task.Run(
                () => ExecuteDriveScanAsync(stream, blockSectionWriter, new ScanDriveInput(request, keepFileNames), progressChannel.Writer,
                    writeLock, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await pumpTask.ConfigureAwait(false);
        }

        await EmitScanCompletionFramesAsync(stream, request, scanOutput, writeLock, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task RunProgressPumpAsync(
        Stream stream,
        ChannelReader<BrokerScanProgress> reader,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastEmit = TimeSpan.Zero;
        var first = true;
        BrokerScanProgress? throttled = null;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var progress))
                {
                    var now = stopwatch.Elapsed;
                    if (first || now - lastEmit >= _progressThrottleInterval)
                    {
                        first = false;
                        lastEmit = now;
                        throttled = null;
                        var progressToEmit = progress;
                        await WriteFrameAsync(stream, writeLock,
                            writer => BrokerProtocol.WriteScanProgress(writer, progressToEmit),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throttled = progress;
                    }
                }
            }

            // The channel completed normally: flush the newest report the throttle window
            // held back (typically the parse phase's records == total report), so the last
            // pre-completion value reaches the client instead of being dropped. Cancellation
            // must not reach this flush - a cancelled scan emits no partial final frame.
            if (throttled is { } pending)
            {
                await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteScanProgress(writer, pending),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // aislop-ignore-next-line SwallowedException -- intentional clean shutdown of progress pump when scan completes or is cancelled
        }
    }

    async Task<(UsnJournalCursor cursor, BlockWriteResult writeResult, TimeSpan scanElapsed, long maximumRecordsProcessed,
        long? totalRecords)> ExecuteDriveScanAsync(
        Stream stream,
        IBlockSectionWriter? blockSectionWriter,
        ScanDriveInput input,
        ChannelWriter<BrokerScanProgress> progressWriter,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        try
        {
            var progressState = new ScanProgressState();
            var progressReporter = new DirectProgress<BlockWriteProgress>(value =>
                progressState.Report(input.Request.Letter, value, progressWriter));

            Task EmitCursorAsync(UsnJournalCursor armedCursor)
            {
                return WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteCursor(writer, input.Request.Letter, armedCursor), cancellationToken);
            }

            var (cursor, writeResult) = await ExecuteBlockScanAsync(input, blockSectionWriter, progressReporter,
                EmitCursorAsync, cancellationToken).ConfigureAwait(false);

            return progressState.Complete(cursor, writeResult);
        }
        finally
        {
            progressWriter.TryComplete();
        }
    }

    readonly record struct ScanDriveInput(ScanDriveRequest Request, IReadOnlyList<string> KeepFileNames);

    async Task EmitScanCompletionFramesAsync(
        Stream stream,
        ScanDriveRequest request,
        (UsnJournalCursor cursor, BlockWriteResult writeResult, TimeSpan scanElapsed, long maximumRecordsProcessed, long?
            totalRecords) scanOutput,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var finalRecords = scanOutput.totalRecords ?? scanOutput.writeResult.RowCount;
        if (finalRecords < scanOutput.maximumRecordsProcessed)
        {
            finalRecords = scanOutput.maximumRecordsProcessed;
        }

        long? finalTotalRecords = scanOutput.totalRecords ?? scanOutput.writeResult.RowCount;
        if (finalTotalRecords.HasValue && finalTotalRecords.Value < finalRecords)
        {
            finalTotalRecords = finalRecords;
        }

        var finalProgress = new BrokerScanProgress
        {
            DriveLetter = request.Letter,
            Phase = BrokerScanPhase.Transferring,
            RecordsProcessed = finalRecords,
            BytesProcessed = scanOutput.writeResult.NamePoolUsedBytes,
            TotalRecords = finalTotalRecords,
            TotalBytes = scanOutput.writeResult.NamePoolUsedBytes,
            Elapsed = scanOutput.scanElapsed
        };

        await WriteFrameAsync(stream, writeLock,
            writer => BrokerProtocol.WriteScanProgress(writer, finalProgress),
            cancellationToken).ConfigureAwait(false);

        await WriteFrameAsync(stream, writeLock,
            writer => BrokerProtocol.WriteScanReady(writer, request.MmfName, scanOutput.writeResult.RowCount,
                scanOutput.writeResult.NamePoolUsedBytes, scanOutput.writeResult.SkippedRecordCount),
            cancellationToken).ConfigureAwait(false);

        UsnJournalEntry[] entries;
        UsnJournalCursor updated;
        try
        {
            (entries, updated) = CatchUp(request.Letter, scanOutput.cursor);
        }
        // The armed cursor can fall outside the journal's live window by the time
        // catch-up runs (a busy system volume's 32 MB journal wrapping past it
        // during a long multi-drive scan). Degrade the same way a (0,0) warm-start
        // cursor does in StreamWatchAsync: watch from the current journal position
        // and tell the caller the gap was lost, instead of failing the whole drive
        // with an Error frame even though the scan itself succeeded. Re-querying
        // the cursor is not wrapped here - if it also throws, that propagates to
        // HandleArmAndScanAsync's per-drive catch, which emits the existing Error
        // frame.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var freshCursor = _queryCursor(request.Letter);
            await WriteFrameAsync(stream, writeLock,
                writer => BrokerProtocol.WriteWarning(writer, request.Letter,
                    $"Catch-up after scan failed: {exception.Message}; watching from the current journal " +
                    "position, changes made during the scan were not replayed"),
                cancellationToken).ConfigureAwait(false);
            entries = Array.Empty<UsnJournalEntry>();
            updated = freshCursor;
        }

        await WriteFrameAsync(stream, writeLock,
            writer => BrokerProtocol.WriteJournalBatch(writer, request.Letter, BrokerFrame.NoArmEpoch, updated, entries),
            cancellationToken).ConfigureAwait(false);
    }

    internal static ScanDriveRequest[] ParseScanSpecForTest(string spec) => ParseScanSpec(spec).ToArray();

    internal static WatchDriveRequest[] ParseWatchSpecForTest(string spec) => ParseWatchSpec(spec).ToArray();

    // Scan tokens are comma-joined "letter:journalId:nextUsn:sectionName:profile".
    // Absent section and profile default to empty and Full.
    static IEnumerable<ScanDriveRequest> ParseScanSpec(string spec)
    {
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':');
            yield return new ScanDriveRequest(
                parts[0],
                ulong.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                parts.Length > 3 ? parts[3] : string.Empty,
                parts.Length > 4 ? ParseScanProfile(parts[4]) : BrokerScanProfile.Full);
        }
    }

    static BrokerScanProfile ParseScanProfile(string value)
    {
        var profile = (BrokerScanProfile)int.Parse(value, CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(profile))
        {
            throw new InvalidDataException($"Unknown broker scan profile: {value}");
        }

        return profile;
    }

    // A per-drive arm-and-scan request: bare drive letter, the resume cursor
    // (unused for arm-and-scan, which queries fresh), the caller-created map name,
    // and optional cold-scan record profile.
    internal readonly record struct ScanDriveRequest(
        string Letter,
        ulong JournalId,
        long NextUsn,
        string MmfName,
        BrokerScanProfile Profile);

    // A per-drive live watch request: bare drive letter, the resume cursor, and the
    // client-issued arm epoch that identifies this generation of the drive's watch stream.
    internal readonly record struct WatchDriveRequest(
        string Letter,
        ulong JournalId,
        long NextUsn,
        uint ArmEpoch);

    static IEnumerable<WatchDriveRequest> ParseWatchSpec(string spec)
    {
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(':');
            if (parts.Length != 4)
            {
                throw new InvalidDataException(
                    $"Watch spec token '{token}' must be letter:journalId:nextUsn:armEpoch.");
            }

            yield return new WatchDriveRequest(
                NormalizeWatchDrive(parts[0]),
                ulong.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                uint.Parse(parts[3], CultureInfo.InvariantCulture));
        }
    }

    // The host normalizes a disarm the same way it normalizes an arm, falling back to
    // the raw string when it is not a drive letter. A string that cannot be normalized
    // can never have been armed either, so it keys itself and the lookup simply misses
    // (the same 'not armed is not an error' rule the disarm already has).
    static string NormalizeWatchDrive(string drive) =>
        JournalBrokerClient.TryNormalizeDriveLetter(drive, out var normalized) ? normalized : drive;
}
