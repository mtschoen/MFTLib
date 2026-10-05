using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     One drive's watch as <see cref="ScriptedWatchSource" /> hands it to the index. The test
///     scripts what its read yields and how it ends; the watch counts its disposals so a test can
///     prove the index disposed it exactly once.
/// </summary>
public sealed class ScriptedDriveWatch : IIndexDriveWatch
{
    // Multi-reader: the read, its exit drain and disposal's drain may dequeue at the same time.
    readonly Channel<PendingItem> _items = Channel.CreateUnbounded<PendingItem>();

    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<bool> _readEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _drainLock = new();
    int _drainsInFlight;
    bool? _readEndedCancelled;
    Exception? _cancellationFailure;
    Exception? _completionFault;
    int _disposeCount;
    volatile bool _readStarted;

    internal ScriptedDriveWatch(IndexWatchTarget target)
    {
        Target = target;
    }

    /// <summary>The drive letter exactly as the index passed it to the source, in whatever case it used.</summary>
    public char DriveLetter => Target.DriveLetter;

    /// <summary>The journal position the index resumed this watch from: the cursor of the block it started on.</summary>
    public UsnJournalCursor StartCursor => new(Target.JournalId, Target.NextUsn);

    /// <summary>How many times the index disposed this watch; the index owes exactly one, and a second disposal throws.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>True once anything has started reading this watch, which only the index's pump does.</summary>
    public bool ReadStarted => _readStarted;

    /// <summary>Completes on the first disposal.</summary>
    public Task Disposed => _disposed.Task;

    /// <summary>
    ///     Completes when the index's read of this watch ends; true when cancellation ended it. By then
    ///     the watch is closed and every unread item has settled.
    /// </summary>
    public Task<bool> ReadEnded => _readEnded.Task;

    internal IndexWatchTarget Target { get; }

    /// <summary>Runs on a closing drain after it dequeues an unread item and before it settles it.</summary>
    internal Action? BeforeSettleUnread { get; set; }

    /// <summary>Delivers one journal batch and completes once the index's pump has taken the item after it.</summary>
    /// <param name="entries">The journal entries the batch carries.</param>
    /// <param name="cursor">The journal position after the batch.</param>
    /// <returns>A task that completes when the pump is done with the batch.</returns>
    /// <exception cref="InvalidOperationException">The watch is closed.</exception>
    /// <exception cref="TimeoutException">The pump did not take the batch within the hang guard.</exception>
    public Task PublishBatchAsync(IReadOnlyList<UsnJournalEntry> entries, UsnJournalCursor cursor) =>
        Publish(new JournalBatch(entries, cursor.JournalId, cursor.NextUsn));

    /// <summary>Queues one journal batch without waiting; the returned task completes when the pump is done with it.</summary>
    /// <param name="entries">The journal entries the batch carries.</param>
    /// <param name="cursor">The journal position after the batch.</param>
    /// <returns>A task that completes when the pump is done with the batch.</returns>
    /// <exception cref="InvalidOperationException">The watch is closed.</exception>
    public Task QueueBatch(IReadOnlyList<UsnJournalEntry> entries, UsnJournalCursor cursor) =>
        Queue(new JournalBatch(entries, cursor.JournalId, cursor.NextUsn));

    /// <summary>Delivers the caught-up marker and completes once the index's pump has taken the item after it.</summary>
    /// <returns>A task that completes when the pump is done with the marker.</returns>
    /// <exception cref="InvalidOperationException">The watch is closed.</exception>
    /// <exception cref="TimeoutException">The pump did not take the marker within the hang guard.</exception>
    public Task PublishCaughtUpAsync() => Publish(new DriveCaughtUp());

    /// <summary>Queues the caught-up marker without waiting; the returned task completes when the pump is done with it.</summary>
    /// <returns>A task that completes when the pump is done with the marker.</returns>
    /// <exception cref="InvalidOperationException">The watch is closed.</exception>
    public Task QueueCaughtUp() => Queue(new DriveCaughtUp());

    /// <summary>The drive's own watch fails: the index reports <see cref="WatchFaultKind.Drive" />.</summary>
    /// <param name="exception">The failure the drive's watch reports.</param>
    public void FailDrive(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var fault = new DriveWatchFaultException(DriveLetter, exception.Message, exception);
        _completionFault = fault;
        _items.Writer.TryComplete(fault);
    }

    /// <summary>The channel is lost: the index reports <see cref="WatchFaultKind.Channel" />.</summary>
    /// <param name="exception">The failure the read throws.</param>
    public void LoseChannel(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _completionFault = exception;
        _items.Writer.TryComplete(exception);
    }

    /// <summary>The read ends normally, which the index also treats as a lost channel.</summary>
    public void End() => _items.Writer.TryComplete();

    /// <summary>
    ///     Makes a cancelled read throw <paramref name="exception" /> instead of
    ///     <see cref="OperationCanceledException" />, the way a pipe closed under a pending read
    ///     surfaces as an I/O error.
    /// </summary>
    /// <param name="exception">The failure a cancelled read throws.</param>
    public void FailOnCancellation(Exception exception) => _cancellationFailure = exception;

    /// <summary>Writes one item and waits until the pump has finished with it.</summary>
    internal Task Publish(WatchStreamItem item) => Queue(item).WaitAsync(ScriptedWatchSource.HangGuard);

    /// <summary>Writes one item and returns the task that completes once the pump has finished with it, without waiting.</summary>
    internal Task Queue(WatchStreamItem item)
    {
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_items.Writer.TryWrite(new PendingItem(item, consumed)))
        {
            throw new InvalidOperationException($"The watch for drive {DriveLetter} is closed.");
        }

        return consumed.Task;
    }

    async IAsyncEnumerable<WatchStreamItem> IIndexDriveWatch.ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _readStarted = true;
        var cancelled = false;
        Exception? readFault = null;
        try
        {
            while (true)
            {
                PendingItem? pending;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    while (!_items.Reader.TryRead(out pending))
                    {
                        if (!await _items.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            yield break;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    if (_cancellationFailure is { } failure)
                    {
                        readFault = failure;
                        throw failure;
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    readFault = ex;
                    throw;
                }

                try
                {
                    yield return pending.Item;
                }
                finally
                {
                    pending.Consumed.TrySetResult();
                }
            }
        }
        finally
        {
            var isCancelled = cancelled || cancellationToken.IsCancellationRequested;
            Close(readFault ?? _completionFault, isCancelled ? cancellationToken : default);
            lock (_drainLock)
            {
                _readEndedCancelled = isCancelled;
            }

            CompleteReadEndedOnceSettled();
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        var count = Interlocked.Increment(ref _disposeCount);
        Close(_completionFault);
        _disposed.TrySetResult();
        if (count > 1)
        {
            throw new InvalidOperationException($"The watch for drive {DriveLetter} was disposed twice.");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     The one closure point every read exit and disposal reach: delivery fails fast from here on,
    ///     and each unread item settles with <paramref name="fault" />, or as cancelled without one.
    ///     A drain counts itself in before it can dequeue, so the read's end waits for every drain
    ///     still settling an item it took.
    /// </summary>
    void Close(Exception? fault, CancellationToken cancellationToken = default)
    {
        lock (_drainLock)
        {
            _drainsInFlight++;
        }

        try
        {
            _items.Writer.TryComplete();
            while (_items.Reader.TryRead(out var pending))
            {
                BeforeSettleUnread?.Invoke();
                if (fault is not null)
                {
                    pending.Consumed.TrySetException(fault);
                }
                else
                {
                    pending.Consumed.TrySetCanceled(cancellationToken);
                }
            }
        }
        finally
        {
            lock (_drainLock)
            {
                _drainsInFlight--;
            }

            CompleteReadEndedOnceSettled();
        }
    }

    /// <summary>Completes <see cref="ReadEnded" /> once the read has ended and no drain is still settling.</summary>
    void CompleteReadEndedOnceSettled()
    {
        bool cancelled;
        lock (_drainLock)
        {
            if (_drainsInFlight > 0 || _readEndedCancelled is not { } outcome)
            {
                return;
            }

            cancelled = outcome;
        }

        _readEnded.TrySetResult(cancelled);
    }

    sealed record PendingItem(WatchStreamItem Item, TaskCompletionSource Consumed);
}
