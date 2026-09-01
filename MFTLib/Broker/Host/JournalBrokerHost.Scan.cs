using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace MFTLib;

public sealed partial class JournalBrokerHost
{
    async Task HandleArmAndScanAsync(
        Stream stream,
        IMmfWriter mmfWriter,
        string drivesSpec,
        IReadOnlyList<string> keepFileNames,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        foreach (var request in ParseScanSpec(drivesSpec))
        {
            try
            {
                await ProcessDriveScanAsync(stream, mmfWriter, request, keepFileNames, writeLock, cancellationToken)
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
                if (message.StartsWith("Scan payload exceeded ", StringComparison.Ordinal))
                {
                    message = FormattableString.Invariant(
                        $"Scan payload for drive {request.Letter} {message["Scan payload ".Length..]}");
                }

                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, request.Letter, message),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    async Task ProcessDriveScanAsync(
        Stream stream,
        IMmfWriter mmfWriter,
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

        (UsnJournalCursor cursor, MmfWriteResult writeResult, TimeSpan scanElapsed, long maxRecordsProcessed, long?
            totalRecords) scanOutput;
        try
        {
            scanOutput = await Task.Run(
                () => ExecuteDriveScanAsync(stream, mmfWriter, request, keepFileNames, progressChannel.Writer,
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

    async Task<(UsnJournalCursor cursor, MmfWriteResult writeResult, TimeSpan scanElapsed, long maxRecordsProcessed,
        long? totalRecords)> ExecuteDriveScanAsync(
        Stream stream,
        IMmfWriter mmfWriter,
        ScanDriveRequest request,
        IReadOnlyList<string> keepFileNames,
        ChannelWriter<BrokerScanProgress> progressWriter,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        try
        {
            var scanStopwatch = Stopwatch.StartNew();
            long maxRecordsProcessed = 0;
            long? totalRecordsKnown = null;
            var progressLock = new object();

            var progressReporter = new DirectProgress<MmfWriteProgress>(p =>
            {
                lock (progressLock)
                {
                    // Non-decreasing, like maxRecordsProcessed below: the parse phase
                    // reports the authoritative total record count for the whole
                    // volume, while the write phase's own final report carries only
                    // the count it actually wrote (smaller by construction, since
                    // unused/deleted MFT entries are filtered out before writing). A
                    // later, smaller total from the write phase must not overwrite an
                    // already-known larger total from the parse phase.
                    if (p.TotalRecords.HasValue && p.TotalRecords.Value > (totalRecordsKnown ?? 0))
                    {
                        if (!totalRecordsKnown.HasValue || p.TotalRecords.Value > totalRecordsKnown.Value)
                        {
                            totalRecordsKnown = p.TotalRecords.Value;
                        }
                    }

                    if (p.RecordsProcessed > maxRecordsProcessed)
                    {
                        maxRecordsProcessed = p.RecordsProcessed;
                    }

                    progressWriter.TryWrite(new BrokerScanProgress(
                        request.Letter,
                        maxRecordsProcessed,
                        p.BytesProcessed,
                        totalRecordsKnown,
                        p.TotalBytes,
                        scanStopwatch.Elapsed));
                }
            });

            var (cursor, batches) = ArmAndScanBatches(request.Letter, progressReporter, cancellationToken);
            var filteredBatches = FilterScanProfile(batches, request.Profile, keepFileNames);

            await WriteFrameAsync(stream, writeLock,
                writer => BrokerProtocol.WriteCursor(writer, request.Letter, cursor),
                cancellationToken).ConfigureAwait(false);

            MmfWriteResult writeResult;
            if (mmfWriter is IStreamingMmfWriter streamingWriter)
            {
                writeResult = streamingWriter.Write(request.MmfName, filteredBatches, progressReporter, cancellationToken);
            }
            else
            {
                var allRecords = filteredBatches.SelectMany(b => b).ToArray();
                var byteLength = mmfWriter.Write(request.MmfName, allRecords);
                writeResult = new MmfWriteResult(allRecords.Length, byteLength);
            }

            scanStopwatch.Stop();
            lock (progressLock)
            {
                return (cursor, writeResult, scanStopwatch.Elapsed, maxRecordsProcessed, totalRecordsKnown);
            }
        }
        finally
        {
            progressWriter.TryComplete();
        }
    }

    async Task EmitScanCompletionFramesAsync(
        Stream stream,
        ScanDriveRequest request,
        (UsnJournalCursor cursor, MmfWriteResult writeResult, TimeSpan scanElapsed, long maxRecordsProcessed, long?
            totalRecords) scanOutput,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var finalRecords = scanOutput.totalRecords ?? scanOutput.writeResult.RecordCount;
        if (finalRecords < scanOutput.maxRecordsProcessed)
        {
            finalRecords = scanOutput.maxRecordsProcessed;
        }

        long? finalTotalRecords = scanOutput.totalRecords ?? scanOutput.writeResult.RecordCount;
        if (finalTotalRecords.HasValue && finalTotalRecords.Value < finalRecords)
        {
            finalTotalRecords = finalRecords;
        }

        var finalProgress = new BrokerScanProgress(
            request.Letter,
            finalRecords,
            scanOutput.writeResult.ByteLength,
            finalTotalRecords,
            scanOutput.writeResult.ByteLength,
            scanOutput.scanElapsed);

        await WriteFrameAsync(stream, writeLock,
            writer => BrokerProtocol.WriteScanProgress(writer, finalProgress),
            cancellationToken).ConfigureAwait(false);

        await WriteFrameAsync(stream, writeLock,
            writer => BrokerProtocol.WriteScanReady(writer, request.MmfName, scanOutput.writeResult.RecordCount,
                scanOutput.writeResult.ByteLength),
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
            writer => BrokerProtocol.WriteJournalBatch(writer, request.Letter, updated, entries),
            cancellationToken).ConfigureAwait(false);
    }

    // Spec tokens are comma-joined "letter:journalId:nextUsn:mmfName". The watch
    // spec omits the map name (three fields); MmfName is then empty.
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
                parts.Length > 4
                    ? ParseScanProfile(parts[4])
                    : BrokerScanProfile.Full);
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

    // internal: ParseScanProfile already rejects undefined values before a request
    // reaches here, so the default arm is unreachable from the wire path; it exists
    // as an exhaustiveness guard for future profile values and is tested directly.
    // keepFileNames is ignored under Full (the complete inventory already includes
    // every file); under DirectoryIndex it names non-directory files to keep
    // alongside every directory, matched case-insensitively against NTFS's default
    // case-insensitive name comparison.
    internal static ScanRecord[] ApplyScanProfile(
        ScanRecord[] records, BrokerScanProfile profile, IReadOnlyCollection<string> keepFileNames)
    {
        return FilterScanProfile([records], profile, keepFileNames).Single().ToArray();
    }

    static IEnumerable<IReadOnlyList<ScanRecord>> FilterScanProfile(
        IEnumerable<IReadOnlyList<ScanRecord>> batches,
        BrokerScanProfile profile,
        IReadOnlyCollection<string>? keepFileNames)
    {
        HashSet<string>? keepSet = null;
        if (profile == BrokerScanProfile.DirectoryIndex && keepFileNames != null && keepFileNames.Count > 0)
        {
            keepSet = new HashSet<string>(keepFileNames, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var batch in batches)
        {
            yield return profile switch
            {
                BrokerScanProfile.Full => batch,
                BrokerScanProfile.DirectoryIndex => FilterDirectoryIndexBatch(batch, keepSet),
                _ => throw new InvalidDataException($"Unknown broker scan profile: {profile}")
            };
        }
    }

    static ScanRecord[] FilterDirectoryIndexBatch(IReadOnlyList<ScanRecord> batch, HashSet<string>? keepSet)
    {
        var result = new List<ScanRecord>(batch.Count);
        foreach (var record in batch)
        {
            if (record.IsDirectory || (keepSet != null && keepSet.Contains(record.Name)))
            {
                result.Add(record);
            }
        }

        return result.ToArray();
    }

    // A per-drive arm-and-scan request: bare drive letter, the resume cursor
    // (unused for arm-and-scan, which queries fresh), the caller-created map name,
    // and an optional cold-scan record profile.
    readonly record struct ScanDriveRequest(
        string Letter,
        ulong JournalId,
        long NextUsn,
        string MmfName,
        BrokerScanProfile Profile);
}
