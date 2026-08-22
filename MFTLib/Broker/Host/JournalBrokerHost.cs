using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace MFTLib;

/// <summary>
///     Elevated-side broker logic: per drive, arm the journal cursor BEFORE
///     scanning (so changes during the scan are replayed by catch-up, closing the
///     cold-start gap), then scan. Volume access is injected so the core is
///     testable without real elevation; <see cref="CreateDefault" /> wires the real
///     MFTLib seams.
/// </summary>
public sealed partial class JournalBrokerHost
{
    readonly UsnJournalCursorQuery _queryCursor;
    readonly UsnJournalCatchUpSource _readJournal;
    readonly ProgressStreamingDriveScanSource _scanDrive;
    readonly JournalBatchSource? _watchDrive;

    internal static TimeSpan ProgressThrottleInterval = TimeSpan.FromMilliseconds(250);

    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        ProgressStreamingDriveScanSource scanDrive,
        UsnJournalCatchUpSource readJournal,
        JournalBatchSource? watchDrive = null)
    {
        _queryCursor = queryCursor;
        _scanDrive = scanDrive;
        _readJournal = readJournal;
        _watchDrive = watchDrive;
    }

    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        StreamingDriveScanSource scanDrive,
        UsnJournalCatchUpSource readJournal,
        JournalBatchSource? watchDrive = null)
        : this(
            queryCursor,
            (drive, _, ct) => scanDrive(drive, ct),
            readJournal,
            watchDrive)
    {
    }

    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        DriveScanSource scanDrive,
        UsnJournalCatchUpSource readJournal,
        JournalBatchSource? watchDrive = null)
        : this(
            queryCursor,
            (drive, _, _) => new[] { scanDrive(drive) },
            readJournal,
            watchDrive)
    {
    }

    /// <summary>
    ///     Arm the journal cursor, then scan. The cursor is captured strictly before
    ///     the scan begins so any file changes that race the scan are caught by the
    ///     subsequent catch-up read instead of being silently missed.
    /// </summary>
    public (UsnJournalCursor Cursor, ScanRecord[] Records) ArmAndScan(string driveLetter)
    {
        var cursor = _queryCursor(driveLetter); // strictly before the scan
        var records = _scanDrive(driveLetter, null, CancellationToken.None).SelectMany(b => b).ToArray();
        return (cursor, records);
    }

    public (UsnJournalCursor Cursor, IEnumerable<IReadOnlyList<ScanRecord>> Batches) ArmAndScanBatches(
        string driveLetter, IProgress<MmfWriteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var cursor = _queryCursor(driveLetter);
        var batches = _scanDrive(driveLetter, progress, cancellationToken);
        return (cursor, batches);
    }

    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) CatchUp(string driveLetter, UsnJournalCursor since)
    {
        return _readJournal(driveLetter, since);
    }


    List<Task> StartWatchTasks(Stream stream, string watchSpec, SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        foreach (var request in ParseScanSpec(watchSpec)) // watch tokens omit the map name
        {
            tasks.Add(StreamWatchAsync(stream, request.Letter,
                new UsnJournalCursor(request.JournalId, request.NextUsn), writeLock, cancellationToken));
        }

        return tasks;
    }

    async Task StreamWatchAsync(Stream stream, string drive, UsnJournalCursor since,
        SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        if (_watchDrive == null)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, "Broker has no watch source"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            // A (0,0) cursor means the caller had no cached cursor for this drive (a warm
            // start with an unknown cursor). Resolve the current cursor and watch from
            // now so the live watch still works; only the pre-launch gap is lost.
            var effectiveSince = since.JournalId == 0 ? _queryCursor(drive) : since;

            // No `.WithCancellation(cancellationToken)` here: cancellationToken is
            // already passed as the explicit third argument above, which the
            // production implementation's `[EnumeratorCancellation]` parameter binds
            // directly - adding it again on the same token is redundant.
            await foreach (var (entries, cursor) in _watchDrive(drive, effectiveSince, cancellationToken)
                               .ConfigureAwait(false))
            {
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteJournalBatch(writer, drive, cursor, entries), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop: the session was cancelled.
        }
        // Surface a genuine watch failure (journal wrapped mid-stream, volume
        // closed) to the caller as a per-drive Error instead of tearing down the
        // session; other drives keep watching. Cancellation is handled above.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, exception.Message), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    static async Task WriteFrameAsync(Stream stream, SemaphoreSlim writeLock,
        Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        if (buffer.WrittenCount >= 5)
        {
            BrokerDiagnostics.LogFrame("write", buffer.WrittenSpan[4], buffer.WrittenCount - 4);
        }

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    static async Task<BrokerFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null; // clean EOF before any byte
        }

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        if (!await ReadExactAsync(stream, frameBytes.AsMemory(4, totalLength), cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Truncated broker frame on pipe");
        }

        if (totalLength >= 1)
        {
            BrokerDiagnostics.LogFrame("read", frameBytes[4], totalLength);
        }

        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    // Fill buffer fully. Returns false on a clean EOF before any byte was read;
    // throws if the stream ends partway through (a corrupt/truncated frame).
    static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Truncated broker frame on pipe");
            }

            read += count;
        }

        return true;
    }

    public static JournalBrokerHost CreateDefault()
    {
        return new JournalBrokerHost(
            QueryCursor,
            ScanDriveBatches,
            ReadJournal,
            WatchAndDisposeAsync);
    }

    static UsnJournalCursor QueryCursor(string drive)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.QueryUsnJournal();
    }

    static IEnumerable<IReadOnlyList<ScanRecord>> ScanDriveBatches(
        string drive, IProgress<MmfWriteProgress>? progress, CancellationToken cancellationToken)
    {
        using var volume = MftVolume.Open(Bare(drive));
        // Adapt MftScanProgress from the native MFT volume parser into MmfWriteProgress
        // so drive scan progress flows to the host progress pump.
        //
        // This is the parse-phase source, reporting RecordsScanned climbing to
        // TotalRecords with BytesProcessed = 0. The write phase is not wired to
        // mid-scan progress (the writer receives null progress in ExecuteDriveScanAsync),
        // and the final byte/record totals are emitted in the guaranteed final frame by
        // EmitScanCompletionFramesAsync.
        //
        // DirectProgress invokes handlers synchronously on the calling thread to avoid
        // thread-pool dispatch delays and ensure TotalRecords is captured reliably.
        var mftProgress = progress != null
            ? new DirectProgress<MftScanProgress>(p =>
                progress.Report(new MmfWriteProgress(
                    p.RecordsScanned,
                    0,
                    p.TotalRecords,
                    null)))
            : null;

        foreach (var batch in volume.ReadRecordBatches(resolvePaths: true, batchSize: 4096, progress: mftProgress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanRecords = ToScanRecords(batch);
            if (scanRecords.Length > 0)
            {
                yield return scanRecords;
            }
        }
    }

    static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.ReadUsnJournal(since);
    }

    static string Bare(string drive)
    {
        return drive.TrimEnd(':', '\\', '/');
    }

    // Open the volume, stream cursor-tagged batches until cancelled, and dispose
    // the volume when the watch ends (mirrors the in-process WatchAndDispose).
    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchAndDisposeAsync(
        string drive, UsnJournalCursor since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var volume = MftVolume.Open(Bare(drive));
        // No .WithCancellation(): WatchUsnJournalWithCursor already takes the token
        // directly and honors it internally.
        await foreach (var batch in volume.WatchUsnJournalWithCursor(since, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    // MftRecord does not carry Size or LastWriteTime on the current MFTLib surface
    // (the in-process MFT scan likewise records Size = 0); ScanRecord keeps those
    // fields for forward compatibility and they are zero from the MFT path. Skip
    // free and path-less records to mirror the direct-scan filter.
    static ScanRecord[] ToScanRecords(MftRecord[] records)
    {
        var result = new List<ScanRecord>(records.Length);
        foreach (var record in records)
        {
            if (!record.InUse || string.IsNullOrEmpty(record.FullPath))
            {
                continue;
            }

            result.Add(new ScanRecord(
                record.RecordNumber, record.ParentRecordNumber, 0,
                0, (uint)record.FileAttributes, record.IsDirectory,
                record.FileName, record.FullPath));
        }

        return result.ToArray();
    }

    // Holds the live watch generation's CTS and per-drive tasks. A StartWatch creates
    // one, an EndWatch (or session end) tears it down; see ServeAsync.
    sealed class WatchGeneration
    {
        public readonly List<Task> Tasks = new();
        public CancellationTokenSource? Cancellation;
    }

    sealed class DirectProgress<T> : IProgress<T>
    {
        readonly Action<T> _handler;

        public DirectProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => _handler(value);
    }
}
