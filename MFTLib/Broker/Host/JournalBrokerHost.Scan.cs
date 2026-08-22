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
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, request.Letter, exception.Message),
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

        (UsnJournalCursor cursor, MmfWriteResult writeResult, TimeSpan scanElapsed, long maxRecordsProcessed, long? totalRecords) scanOutput;
        try
        {
            scanOutput = await Task.Run(
                () => ExecuteDriveScanAsync(stream, mmfWriter, request, keepFileNames, progressChannel.Writer, writeLock, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await pumpTask.ConfigureAwait(false);
        }

        await EmitScanCompletionFramesAsync(stream, request, scanOutput, writeLock, cancellationToken).ConfigureAwait(false);
    }

    static async Task RunProgressPumpAsync(
        Stream stream,
        ChannelReader<BrokerScanProgress> reader,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastEmit = TimeSpan.Zero;
        var first = true;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var progress))
                {
                    var now = stopwatch.Elapsed;
                    if (first || now - lastEmit >= ProgressThrottleInterval)
                    {
                        first = false;
                        lastEmit = now;
                        var progressToEmit = progress;
                        await WriteFrameAsync(stream, writeLock,
                            writer => BrokerProtocol.WriteScanProgress(writer, progressToEmit),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // aislop-ignore-next-line SwallowedException -- intentional clean shutdown of progress pump when scan completes or is cancelled
        }
    }

    async Task<(UsnJournalCursor cursor, MmfWriteResult writeResult, TimeSpan scanElapsed, long maxRecordsProcessed, long? totalRecords)> ExecuteDriveScanAsync(
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
                    if (p.TotalRecords.HasValue)
                    {
                        totalRecordsKnown = p.TotalRecords.Value;
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
                writeResult = streamingWriter.Write(request.MmfName, filteredBatches, null, cancellationToken);
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
        (UsnJournalCursor cursor, MmfWriteResult writeResult, TimeSpan scanElapsed, long maxRecordsProcessed, long? totalRecords) scanOutput,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        long finalRecords = scanOutput.totalRecords ?? scanOutput.writeResult.RecordCount;
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
            writer => BrokerProtocol.WriteScanReady(writer, request.MmfName, scanOutput.writeResult.RecordCount, scanOutput.writeResult.ByteLength),
            cancellationToken).ConfigureAwait(false);

        var (entries, updated) = CatchUp(request.Letter, scanOutput.cursor);
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
        return FilterScanProfile(new[] { records }, profile, keepFileNames).Single().ToArray();
    }

    internal static IEnumerable<IReadOnlyList<ScanRecord>> FilterScanProfile(
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
