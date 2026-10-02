using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     One drive's watch handle as <see cref="FakeIndexWatchSource" /> hands it out. The test
///     scripts what its read yields and how it ends; the handle counts its disposals so a test can
///     prove the index disposed it exactly once.
/// </summary>
internal sealed class ScriptedDriveWatch : IIndexDriveWatch
{
    readonly Channel<PendingItem> _items =
        Channel.CreateUnbounded<PendingItem>(new UnboundedChannelOptions { SingleReader = true });

    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? _cancellationFailure;
    int _disposeCount;

    public ScriptedDriveWatch(IndexWatchTarget target)
    {
        Target = target;
    }

    public char DriveLetter => Target.DriveLetter;

    /// <summary>The cursor the index started this watch from.</summary>
    public IndexWatchTarget Target { get; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <summary>Set once anything has started reading this handle, which only a pump does.</summary>
    public bool ReadStarted { get; private set; }

    /// <summary>Completes on the first disposal.</summary>
    public Task Disposed => _disposed.Task;

    /// <summary>Writes one item and waits until the pump has finished with it.</summary>
    public Task Publish(WatchStreamItem item) => Queue(item).WaitAsync(FakeIndexWatchSource.HangGuard);

    /// <summary>
    ///     Writes one item and returns the task that completes once the pump has finished with it
    ///     (asked for the next item), without waiting.
    /// </summary>
    public Task Queue(WatchStreamItem item)
    {
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _items.Writer.TryWrite(new PendingItem(item, consumed));
        return consumed.Task;
    }

    /// <summary>The drive's own watch fails: the read throws <see cref="DriveWatchFaultException" />.</summary>
    public void FailDrive(Exception exception)
    {
        _items.Writer.TryComplete(new DriveWatchFaultException(DriveLetter, exception.Message, exception));
    }

    /// <summary>The channel carrying the watch is lost: the read throws <paramref name="exception" />.</summary>
    public void LoseChannel(Exception exception) => _items.Writer.TryComplete(exception);

    /// <summary>The read ends normally, which no stop asked for.</summary>
    public void End() => _items.Writer.TryComplete();

    /// <summary>
    ///     Makes a cancelled read throw <paramref name="exception" /> instead of
    ///     <see cref="OperationCanceledException" />, the way a pipe closed under a pending read
    ///     surfaces as an I/O error.
    /// </summary>
    public void FailOnCancellation(Exception exception) => _cancellationFailure = exception;

    public async IAsyncEnumerable<WatchStreamItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ReadStarted = true;
        while (true)
        {
            PendingItem pending;
            try
            {
                if (!await _items.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield break;
                }

                if (!_items.Reader.TryRead(out pending!))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (_cancellationFailure is { } failure)
            {
                throw failure;
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

    public ValueTask DisposeAsync()
    {
        var count = Interlocked.Increment(ref _disposeCount);
        _items.Writer.TryComplete();
        _disposed.TrySetResult();
        if (count > 1)
        {
            throw new InvalidOperationException($"The watch handle for drive {DriveLetter} was disposed twice.");
        }

        return ValueTask.CompletedTask;
    }

    sealed record PendingItem(WatchStreamItem Item, TaskCompletionSource Consumed);
}
