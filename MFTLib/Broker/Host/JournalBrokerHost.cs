using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MFTLib;

/// <summary>
/// Elevated-side broker logic: per drive, arm the journal cursor BEFORE
/// scanning (so changes during the scan are replayed by catch-up, closing the
/// cold-start gap), then scan. Volume access is injected so the core is
/// testable without real elevation; <see cref="CreateDefault"/> wires the real
/// MFTLib seams.
/// </summary>
public sealed partial class JournalBrokerHost
{
    readonly UsnJournalCursorQuery _queryCursor;
    readonly DriveScanSource _scanDrive;
    readonly UsnJournalCatchUpSource _readJournal;
    readonly JournalBatchSource? _watchDrive;

    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        DriveScanSource scanDrive,
        UsnJournalCatchUpSource readJournal,
        JournalBatchSource? watchDrive = null)
    {
        _queryCursor = queryCursor;
        _scanDrive = scanDrive;
        _readJournal = readJournal;
        _watchDrive = watchDrive;
    }

    /// <summary>
    /// Arm the journal cursor, then scan. The cursor is captured strictly before
    /// the scan begins so any file changes that race the scan are caught by the
    /// subsequent catch-up read instead of being silently missed.
    /// </summary>
    public (UsnJournalCursor Cursor, ScanRecord[] Records) ArmAndScan(string driveLetter)
    {
        var cursor = _queryCursor(driveLetter); // strictly before the scan
        var records = _scanDrive(driveLetter);
        return (cursor, records);
    }

    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) CatchUp(string driveLetter, UsnJournalCursor since)
        => _readJournal(driveLetter, since);

    List<Task> StartWatchTasks(Stream stream, string watchSpec, SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        foreach (var request in ParseScanSpec(watchSpec)) // watch tokens omit the map name
            tasks.Add(StreamWatchAsync(stream, request.Letter,
                new UsnJournalCursor(request.JournalId, request.NextUsn), writeLock, cancellationToken));
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

    async Task HandleArmAndScanAsync(Stream stream, IMmfWriter mmfWriter, string drivesSpec,
        IReadOnlyList<string> keepFileNames, SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        foreach (var request in ParseScanSpec(drivesSpec))
        {
            try
            {
                var (cursor, records) = ArmAndScan(request.Letter);
                records = ApplyScanProfile(records, request.Profile, keepFileNames);
                await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteCursor(writer, request.Letter, cursor), cancellationToken)
                    .ConfigureAwait(false);

                var byteLength = mmfWriter.Write(request.MmfName, records);
                await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteScanReady(writer, request.MmfName, records.Length, byteLength), cancellationToken)
                    .ConfigureAwait(false);

                var (entries, updated) = CatchUp(request.Letter, cursor);
                await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteJournalBatch(writer, request.Letter, updated, entries), cancellationToken)
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
                    writer => BrokerProtocol.WriteError(writer, request.Letter, exception.Message), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    // A per-drive arm-and-scan request: bare drive letter, the resume cursor
    // (unused for arm-and-scan, which queries fresh), the caller-created map name,
    // and an optional cold-scan record profile.
    readonly record struct ScanDriveRequest(
        string Letter, ulong JournalId, long NextUsn, string MmfName, BrokerScanProfile Profile);

    // Holds the live watch generation's CTS and per-drive tasks. A StartWatch creates
    // one, an EndWatch (or session end) tears it down; see ServeAsync.
    sealed class WatchGeneration
    {
        public CancellationTokenSource? Cancellation;
        public readonly List<Task> Tasks = new();
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
            throw new InvalidDataException($"Unknown broker scan profile: {value}");
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
        ScanRecord[] records, BrokerScanProfile profile, IReadOnlyCollection<string> keepFileNames) =>
        profile switch
        {
            BrokerScanProfile.Full => records,
            BrokerScanProfile.DirectoryIndex => FilterDirectoryIndex(records, keepFileNames),
            _ => throw new InvalidDataException($"Unknown broker scan profile: {profile}"),
        };

    static ScanRecord[] FilterDirectoryIndex(ScanRecord[] records, IReadOnlyCollection<string> keepFileNames)
    {
        var keepSet = new HashSet<string>(keepFileNames, StringComparer.OrdinalIgnoreCase);
        return records.Where(record => record.IsDirectory || keepSet.Contains(record.Name)).ToArray();
    }

    static async Task WriteFrameAsync(Stream stream, SemaphoreSlim writeLock,
        Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        if (buffer.WrittenCount >= 5)
            BrokerDiagnostics.LogFrame("write", buffer.WrittenSpan[4], buffer.WrittenCount - 4);
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
            return null; // clean EOF before any byte

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        if (!await ReadExactAsync(stream, frameBytes.AsMemory(4, totalLength), cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated broker frame on pipe");

        if (totalLength >= 1)
            BrokerDiagnostics.LogFrame("read", frameBytes[4], totalLength);
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
                    return false;
                throw new EndOfStreamException("Truncated broker frame on pipe");
            }
            read += count;
        }
        return true;
    }

    public static JournalBrokerHost CreateDefault() => new(
        queryCursor: QueryCursor,
        scanDrive: ScanDrive,
        readJournal: ReadJournal,
        watchDrive: WatchAndDisposeAsync);

    static UsnJournalCursor QueryCursor(string drive)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.QueryUsnJournal();
    }

    static ScanRecord[] ScanDrive(string drive)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return ToScanRecords(volume.ReadAllRecords(resolvePaths: true));
    }

    static (UsnJournalEntry[] Entries, UsnJournalCursor Updated) ReadJournal(string drive, UsnJournalCursor since)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.ReadUsnJournal(since);
    }

    static string Bare(string drive) => drive.TrimEnd(':', '\\', '/');

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
                continue;
            result.Add(new ScanRecord(
                record.RecordNumber, record.ParentRecordNumber, Size: 0,
                LastWriteTicks: 0, (uint)record.FileAttributes, record.IsDirectory,
                record.FileName, record.FullPath));
        }
        return result.ToArray();
    }
}
