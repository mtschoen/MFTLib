using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Bridges the broker's per-drive live-watch enumerables onto the single merged stream
///     <see cref="IIndexWatchSource" /> declares, and arms and disarms one drive at a time on a
///     stream that is already running. The client is borrowed, never owned: the same elevated
///     connection that produced the blocks watches them.
/// </summary>
public sealed partial class BrokerIndexWatchSource : IIndexWatchSource
{
    readonly Func<CancellationToken, Task<JournalBrokerClient>> _connectAsync;
    readonly Lock _streamLock = new();
    readonly Dictionary<char, Task> _readersByDrive = [];

    // This source's own per-drive counter, not the wire arm epoch. They never meet: the epoch is
    // assigned by JournalBrokerClient and consumed by its demux, while this is assigned and
    // consumed inside this class. Nothing here reads, writes, or forwards an epoch.
    readonly Dictionary<char, int> _armGenerationsByDrive = [];
    readonly HashSet<char> _stopRequestedDrives = [];

    // Drives that are out of the reader map because they are being replaced. A rescan holds one
    // here from its disarm until its re-arm, which spans the whole of an MFT scan, and another
    // drive faulting in that window must not read the map emptying as the last reader leaving.
    // Tracked apart from _stopRequestedDrives, which a disarm leaves set until an arm clears it,
    // so a drive disarmed for good cannot keep the stream from ever ending.
    readonly HashSet<char> _drivesAwaitingReader = [];

    bool _streamClaimed;

    // The four things a running stream owns, held together so their nullability is one question
    // asked once rather than four the per-drive members would each have to assert away.
    LiveStream? _stream;

    public BrokerIndexWatchSource(Func<CancellationToken, Task<JournalBrokerClient>> connectAsync)
    {
        _connectAsync = connectAsync;
    }

    public async IAsyncEnumerable<WatchStreamItem> StartWatching(
        IReadOnlyList<IndexWatchTarget> targets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateTargets(targets);
        cancellationToken.ThrowIfCancellationRequested();
        ClaimStream();

        LiveStream stream;
        Channel<TaggedItem> channel;
        try
        {
            var client = await _connectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Keep the demux alive until StopLiveWatchAsync reads EndWatchAck. Cancelling it with
            // the consumer token can leave that acknowledgement to terminate the next watch.
            await client.SendStartWatchAsync(BuildCursors(targets), CancellationToken.None)
                .ConfigureAwait(false);
            channel = Channel.CreateUnbounded<TaggedItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            stream = new LiveStream(client, client.CreateBatchSource(), channel.Writer,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            StartStream(stream, targets);
        }
        catch
        {
            ReleaseStream();
            throw;
        }

        try
        {
            await foreach (var tagged in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Anything a superseded arm queued here is stale by definition: the drive it
                // belongs to has since been disarmed or re-armed, and applying it would drag that
                // drive's cursor backwards behind a block a rescan has already replaced. The
                // client's own arm epoch drops what was still on the wire; this drops what had
                // already crossed it and was sitting in this channel when the drive was retired.
                // A DriveWatchFailure is filtered the same way, because a failure the drive has
                // already been re-armed out of no longer describes anything true about it.
                if (tagged.ArmGeneration != CurrentArmGeneration(tagged.DriveLetter))
                {
                    continue;
                }

                yield return tagged.Item;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            await StopStreamAsync(stream).ConfigureAwait(false);
        }
    }

    static void ValidateTargets(IReadOnlyList<IndexWatchTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var driveLetters = new HashSet<char>();
        foreach (var target in targets)
        {
            if (!driveLetters.Add(char.ToUpperInvariant(target.DriveLetter)))
            {
                // Two cursors for one drive have no meaning and the dictionary below would throw a
                // duplicate-key ArgumentException naming neither the parameter nor the drive.
                throw new ArgumentException(
                    $"Drive {target.DriveLetter} appears more than once in the watch targets.", nameof(targets));
            }
        }
    }

    static Dictionary<string, UsnJournalCursor> BuildCursors(IReadOnlyList<IndexWatchTarget> targets)
    {
        return targets.ToDictionary(
            target => JournalBrokerClient.NormalizeDriveLetter(target.DriveLetter.ToString()),
            target => new UsnJournalCursor(target.JournalId, target.NextUsn));
    }

    void ClaimStream()
    {
        lock (_streamLock)
        {
            if (_streamClaimed)
            {
                throw new InvalidOperationException(
                    "This watch source is already running a stream. One instance runs at most one at a time.");
            }

            _streamClaimed = true;
        }
    }

    void StartStream(LiveStream stream, IReadOnlyList<IndexWatchTarget> targets)
    {
        lock (_streamLock)
        {
            _stream = stream;
            foreach (var target in targets)
            {
                StartDriveReaderLocked(stream, target, char.ToUpperInvariant(target.DriveLetter));
            }
        }

        if (targets.Count == 0)
        {
            // No reader exists that could ever end this stream, so nothing else can complete it.
            stream.Writer.TryComplete();
        }
    }

    void StartDriveReaderLocked(LiveStream stream, IndexWatchTarget target, char driveLetter)
    {
        _readersByDrive[driveLetter] = ReadDriveAsync(stream.BatchSource, target,
            _armGenerationsByDrive.GetValueOrDefault(driveLetter), stream.Writer,
            stream.ReaderCancellation.Token);
    }

    async Task ReadDriveAsync(JournalBatchSource batchSource, IndexWatchTarget target, int armGeneration,
        ChannelWriter<TaggedItem> writer, CancellationToken cancellationToken)
    {
        var driveLetter = char.ToUpperInvariant(target.DriveLetter);
        var normalizedDrive = JournalBrokerClient.NormalizeDriveLetter(driveLetter.ToString());
        var cursor = new UsnJournalCursor(target.JournalId, target.NextUsn);
        try
        {
            await foreach (var batch in batchSource(normalizedDrive, cursor, cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(new TaggedItem(
                    new JournalBatch(driveLetter, batch.Entries, batch.Cursor.JournalId, batch.Cursor.NextUsn),
                    driveLetter, armGeneration), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One drive's failure ends only that drive. Writing it as an item keeps the merged
            // stream alive for every other drive, which is what the host and the client's
            // per-drive channels already do underneath.
            writer.TryWrite(new TaggedItem(new DriveWatchFailure(driveLetter, exception), driveLetter,
                armGeneration));

            // Unless this was the last reader. A failure is not the generation ending, so the
            // client completes nothing else, and with no reader left nothing can produce another
            // item on this stream; leaving the channel open would hang the consumer instead of
            // ending it. The index answers the same case the same way, by ending its pump once
            // every watched drive has faulted and starting a fresh session to recover.
            if (WasLastReader(driveLetter))
            {
                writer.TryComplete();
            }

            return;
        }

        // A clean end with no stop asked for this drive means the client completed every channel
        // at once, which is the generation ending, so the merged stream ends with it. A drive that
        // was disarmed or re-armed was told to stop, and returning quietly is what stops one
        // drive's replacement from ending every other drive's watch.
        if (!IsStopRequested(driveLetter))
        {
            writer.TryComplete();
        }
    }

    async Task StopStreamAsync(LiveStream stream)
    {
        await stream.ReaderCancellation.CancelAsync().ConfigureAwait(false);
        Task[] readers;
        lock (_streamLock)
        {
            readers = [.. _readersByDrive.Values];
        }

        try
        {
            await stream.Client.StopLiveWatchAsync().ConfigureAwait(false);
        }
        finally
        {
            await WaitForReadersAsync(readers).ConfigureAwait(false);
            stream.ReaderCancellation.Dispose();
            ReleaseStream();
        }
    }

    static async Task WaitForReadersAsync(Task[] readers)
    {
        try
        {
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelling every reader is how this stream ends, so it is not a failure. Nothing
            // else can escape ReadDriveAsync, which writes any other failure as an item.
        }
    }

    void ReleaseStream()
    {
        lock (_streamLock)
        {
            _streamClaimed = false;
            _stream = null;
            _readersByDrive.Clear();
            _armGenerationsByDrive.Clear();
            _stopRequestedDrives.Clear();
            _drivesAwaitingReader.Clear();
        }
    }

    LiveStream ClaimedStreamLocked(string operation)
    {
        return _stream ?? throw new InvalidOperationException(
            $"No stream is running on this watch source, so there is no drive to {operation} on it.");
    }

    int CurrentArmGeneration(char driveLetter)
    {
        lock (_streamLock)
        {
            return _armGenerationsByDrive.GetValueOrDefault(driveLetter);
        }
    }

    /// <summary>
    ///     The task draining one drive's batches onto the merged stream, or null when that drive
    ///     has no reader armed. Exists so a test can order itself after a faulting reader's own
    ///     exit: that reader publishes its failure item before it decides whether it was the last
    ///     one, so observing the item says nothing about the decision, and waiting on this task
    ///     does. Nothing in production reaches for a single drive's reader; the stream awaits them
    ///     all together when it stops.
    /// </summary>
    internal Task? DriveReaderForTest(char driveLetter)
    {
        lock (_streamLock)
        {
            return _readersByDrive.GetValueOrDefault(char.ToUpperInvariant(driveLetter));
        }
    }

    bool IsStopRequested(char driveLetter)
    {
        lock (_streamLock)
        {
            return _stopRequestedDrives.Contains(driveLetter);
        }
    }

    /// <summary>
    ///     Retires a failed drive's reader and reports whether the stream has any left. A drive
    ///     whose stop was requested reports false however empty the map looks: a re-arm took its
    ///     reader out already and is about to put a fresh one back. So does any drive still
    ///     awaiting a reader, because the map being empty then says a replacement is in progress,
    ///     not that the stream is over.
    /// </summary>
    bool WasLastReader(char driveLetter)
    {
        lock (_streamLock)
        {
            if (_stopRequestedDrives.Contains(driveLetter))
            {
                return false;
            }

            _readersByDrive.Remove(driveLetter);
            return _readersByDrive.Count == 0 && _drivesAwaitingReader.Count == 0;
        }
    }

    readonly record struct TaggedItem(WatchStreamItem Item, char DriveLetter, int ArmGeneration);

    /// <summary>Everything one running stream owns, so a per-drive member reaches it in one read.</summary>
    sealed record LiveStream(JournalBrokerClient Client, JournalBatchSource BatchSource,
        ChannelWriter<TaggedItem> Writer, CancellationTokenSource ReaderCancellation);
}
