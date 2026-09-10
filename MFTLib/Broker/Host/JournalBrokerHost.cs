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
    readonly MftRecordBatchSource _scanDrive;
    readonly UsnJournalCatchUpSource _readJournal;
    readonly JournalBatchSource? _watchDrive;
    readonly NtfsVolumeInformationQuery? _queryVolumeInfo;

    internal static TimeSpan _progressThrottleInterval = TimeSpan.FromMilliseconds(250);

    public JournalBrokerHost(
        UsnJournalCursorQuery queryCursor,
        MftRecordBatchSource scanDrive,
        UsnJournalCatchUpSource readJournal,
        JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null)
    {
        _queryCursor = queryCursor;
        _scanDrive = scanDrive;
        _readJournal = readJournal;
        _watchDrive = watchDrive;
        _queryVolumeInfo = queryVolumeInfo;
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
        if (_watchDrive == null)
        {
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, request.ArmEpoch, "Broker has no watch source"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var yieldedAny = false;
        try
        {
            // A (0,0) cursor means the caller had no cached cursor for this drive (a warm
            // start with an unknown cursor). Resolve the current cursor and watch from
            // now so the live watch still works; only the pre-launch gap is lost, and
            // there is no cached cursor that could have gone stale.
            var effectiveSince = since.JournalId == 0 ? _queryCursor(drive) : since;

            // No `.WithCancellation(cancellationToken)` here: cancellationToken is
            // already passed as the explicit third argument above, which the
            // production implementation's `[EnumeratorCancellation]` parameter binds
            // directly - adding it again on the same token is redundant.
            await foreach (var (entries, cursor) in _watchDrive(drive, effectiveSince, cancellationToken)
                               .ConfigureAwait(false))
            {
                yieldedAny = true;
                await WriteFrameAsync(stream, writeLock,
                        writer => BrokerProtocol.WriteJournalBatch(writer, drive, request.ArmEpoch, cursor, entries), cancellationToken)
                    .ConfigureAwait(false);
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
            await WriteFrameAsync(stream, writeLock,
                    writer => BrokerProtocol.WriteError(writer, drive, request.ArmEpoch,
                        DescribeWatchFailure(drive, since, yieldedAny, exception)), CancellationToken.None)
                .ConfigureAwait(false);
        }
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
            QueryVolumeInfo);
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
