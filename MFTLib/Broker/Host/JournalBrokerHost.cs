using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using MFTLib.Index;

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
    readonly MftRecordBatchSource _scanDrive;
    readonly UsnJournalCatchUpSource _readJournal;
    readonly JournalBatchSource? _watchDrive;
    readonly NtfsVolumeInformationQuery? _queryVolumeInfo;
    readonly GrowUsnJournalQuery? _growUsnJournal;

    internal static TimeSpan _progressThrottleInterval = TimeSpan.FromMilliseconds(250);

    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        MftRecordBatchSource scanDrive,
        UsnJournalCatchUpSource readJournal,
        JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null,
        GrowUsnJournalQuery? growUsnJournal = null)
    {
        _queryCursor = queryCursor;
        _scanDrive = scanDrive;
        _readJournal = readJournal;
        _watchDrive = watchDrive;
        _queryVolumeInfo = queryVolumeInfo;
        _growUsnJournal = growUsnJournal;
    }

    public (UsnJournalEntry[] Entries, UsnJournalCursor Updated) CatchUp(string driveLetter, UsnJournalCursor since)
    {
        return _readJournal(driveLetter, since);
    }

    async Task StreamWatchAsync(Stream stream, WatchDriveRequest request, SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        var drive = request.Letter;
        var since = new UsnJournalCursor(request.JournalId, request.NextUsn);
        var yieldedAny = false;
        try
        {
            if (_watchDrive == null)
            {
                await TryWriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteError(writer, drive, request.ArmEpoch, "Broker has no watch source"),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // ArmWatchAsync returns null, and every frame write below returns false, when a
            // write found the client's end of the pipe gone. That is the normal way a broker
            // session ends and not a watch failure, so this drive's watch returns here and
            // its task completes instead of faulting: nothing can be reported to a client
            // that is gone.
            if (await ArmWatchAsync(stream, request, writeLock, since, cancellationToken)
                    .ConfigureAwait(false) is not { } arm)
            {
                return;
            }

            // No `.WithCancellation(cancellationToken)` here: cancellationToken is
            // already passed as the explicit third argument above, which the
            // production implementation's `[EnumeratorCancellation]` parameter binds
            // directly - adding it again on the same token is redundant.
            // While diagnostics are enabled, drop the diagnostics logs' own journal
            // entries: each shipped frame is logged, the log write produces a USN record,
            // and that record would ship as another frame. A batch the filter leaves empty
            // is skipped entirely - shipping it would keep the loop alive, because even an
            // empty frame gets logged.
            var logFilter = BrokerDiagnostics.CreateLogFilter();
            var caughtUpReported = arm.CaughtUpReported;
            await foreach (var (entries, cursor) in _watchDrive(drive, arm.Since, cancellationToken)
                               .ConfigureAwait(false))
            {
                yieldedAny = true;
                var filtered = logFilter?.Filter(drive, entries) ?? entries;
                if (filtered.Length > 0 &&
                    !await TryWriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteJournalBatch(writer, drive, request.ArmEpoch, cursor, filtered),
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!caughtUpReported && cursor.JournalId == arm.Tip.JournalId &&
                    cursor.NextUsn >= arm.Tip.NextUsn)
                {
                    if (!await TryWriteFrameAsync(stream, writeLock,
                            writer => BrokerProtocol.WriteCaughtUp(writer, drive, request.ArmEpoch),
                            cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    caughtUpReported = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop: this drive was disarmed, or the whole session was cancelled.
        }
        // Every other failure ends this drive's stream and travels as this drive's Error
        // frame; the other drives keep watching. There is no resume from the current
        // journal position, because a consumer that applied batches from the far side of
        // a lost replay gap would advance past USN records nothing will ever replay and
        // diverge from the volume in silence. A rescan is the only recovery.
        catch (Exception exception)
        {
            // A false result means the client disconnected between the failure and this
            // report, so there is no one left to receive the Error frame and this watch ends
            // quietly, exactly as the write-side returns above do.
            await TryWriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, request.ArmEpoch,
                        DescribeWatchFailure(drive, since, yieldedAny, exception)), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    // Resolves what bounds this arm's backlog and reports the leading CaughtUp marker when
    // the armed cursor already sits at the journal tip. Returns null when that marker's
    // write found the client's pipe already gone, which ends the watch quietly.
    async Task<(UsnJournalCursor Tip, UsnJournalCursor Since, bool CaughtUpReported)?> ArmWatchAsync(
        Stream stream, WatchDriveRequest request, SemaphoreSlim writeLock, UsnJournalCursor since,
        CancellationToken cancellationToken)
    {
        var drive = request.Letter;

        // The journal tip at arm time is what bounds this arm's backlog: every record up to
        // it must be delivered before the drive can be called caught up. One query per arm,
        // and the same query resolves a (0,0) sentinel into a watch-from-now cursor, so only
        // the pre-launch gap is lost, and there is no cached cursor that could have gone stale.
        var tip = _queryCursor(drive);
        var effectiveSince = since.JournalId == 0 ? tip : since;

        // No backlog at all: the drive starts on live entries, so the marker leads. The
        // journal id guard runs on both comparisons: two journals' USN offsets are not
        // comparable, and a mismatched tip belongs to a dead journal generation.
        var caughtUpReported = effectiveSince.JournalId == tip.JournalId &&
                               effectiveSince.NextUsn >= tip.NextUsn;
        if (caughtUpReported &&
            !await TryWriteFrameAsync(stream, writeLock,
                writer => BrokerProtocol.WriteCaughtUp(writer, drive, request.ArmEpoch), cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return (tip, effectiveSince, caughtUpReported);
    }

    // A cached cursor can fall outside the journal's live window before StartWatch is
    // called: a default 32 MB journal wrapping within minutes on a busy system drive, or
    // the journal being recreated with a new id. That failure names the cursor and the
    // rescan, so a consumer can tell "this drive needs rebuilding" from "this drive hit
    // an access or volume error". A failure after batches have flowed, or from a (0,0)
    // sentinel start that had no cached cursor to be stale, carries its own message.
    static string DescribeWatchFailure(string drive, UsnJournalCursor since, bool yieldedAny, Exception exception)
    {
        if (yieldedAny || since.JournalId == 0 || !IsJournalCursorException(exception))
        {
            return exception.Message;
        }

        var cursorText = FormattableString.Invariant($"{since.JournalId}:{since.NextUsn}");
        return $"Drive {drive} cannot resume its live watch from journal cursor {cursorText}: " +
               $"{exception.Message}. The records between that cursor and the current journal position " +
               "are gone, so this drive needs a rescan before it can be watched again.";
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

    // WriteFrameAsync with the broken pipe translated into a false result: the write failed
    // because the client end of the pipe is gone (IOException: "Pipe is broken" /
    // ERROR_NO_DATA), which is how a broker session normally ends rather than a failure. The
    // watch loop reads false as "stop this drive's watch quietly so its task completes
    // instead of faulting"; WriteReplyFrameAsync reads it as "the session is over". Everything
    // else WriteFrameAsync can throw (cancellation, frame serialization) propagates unchanged,
    // and only a frame write is translated: a real drive fault - InvalidOperationException
    // from the journal query or watch, or FileUtilities' "Unable to open volume" IOException
    // - never passes through this wrapper, so it keeps travelling as this drive's Error frame.
    static async Task<bool> TryWriteFrameAsync(Stream stream, SemaphoreSlim writeLock,
        Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        try
        {
            await WriteFrameAsync(stream, writeLock, write, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            // The client closed its end of the pipe; no frame can reach it any more.
            return false;
        }
    }

    // WriteFrameAsync for the request/response paths. They have no per-drive watch to stop
    // quietly: a reply that cannot reach the client ends the whole session, because there is
    // nobody left to serve and no failure left to explain. Throwing is what lets that one
    // fact unwind every nested step of a request - the scan pipeline, its progress pump, and
    // the per-drive catches that would otherwise report it as one more drive's Error frame -
    // up to ServeAsync, which translates it into a normal return.
    static async Task WriteReplyFrameAsync(Stream stream, SemaphoreSlim writeLock,
        Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        if (!await TryWriteFrameAsync(stream, writeLock, write, cancellationToken).ConfigureAwait(false))
        {
            throw new ClientDisconnectedException();
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
            ScanDriveRecordBatches,
            ReadJournal,
            WatchAndDisposeAsync,
            QueryVolumeInfo,
            GrowUsnJournal);
    }

    static UsnJournalSettings GrowUsnJournal(string drive, long maximumSize, long allocationDelta)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.GrowUsnJournal(maximumSize, allocationDelta);
    }

    static UsnJournalCursor QueryCursor(string drive)
    {
        using var volume = MftVolume.Open(Bare(drive));
        return volume.QueryUsnJournal();
    }

    // NtfsVolumeInformation.Query is [SupportedOSPlatform("windows")]; the explicit
    // OperatingSystem.IsWindows() guard (rather than marking this method or its callers
    // windows-only) lets a broker built for this cross-platform library still throw a
    // clear PlatformNotSupportedException on a non-Windows host instead of failing to
    // compile there - the native MFT scan and journal paths already tolerate Linux via
    // the CMake-built shared library, and this keeps that tolerance for the rest of the
    // host even though this one IOCTL is genuinely Windows-only.
    static NtfsVolumeInformation QueryVolumeInfo(string drive)
    {
        return OperatingSystem.IsWindows()
            ? NtfsVolumeInformation.Query(Bare(drive))
            : throw new PlatformNotSupportedException(
                "NTFS volume information queries require Windows (FSCTL_GET_NTFS_VOLUME_DATA).");
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

    static bool IsJournalCursorException(Exception exception)
    {
        var message = exception.Message;
        return !string.IsNullOrEmpty(message) &&
               (message.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("wrapped", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("rescan", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not active", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("deletion", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("recreated", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cursor", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("1181", StringComparison.Ordinal) ||
                message.Contains("1179", StringComparison.Ordinal) ||
                message.Contains("1178", StringComparison.Ordinal));
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
