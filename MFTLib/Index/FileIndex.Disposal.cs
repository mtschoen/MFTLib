using System.Runtime.ExceptionServices;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    TaskCompletionSource? _disposalCompletion;
    /// <summary>
    ///     Closes this view, cancels its queries and waits, and drains its own borrows. The last view stops
    ///     the shared watch and recovery, releases current and retired mappings, and releases canonical ownership.
    ///     Watch faults are reported through WatchFaulted rather than thrown by disposal.
    /// </summary>
    /// <remarks>
    ///     Suspended enumerators retain borrows: dispose them promptly. Active queries observe cancellation at
    ///     most every 4096 rows. Children uses only its caller's token, so disposal waits for that listing.
    ///     FileEntry property reads check release state per access and do not take a borrow.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (RejectInsideHandler(nameof(DisposeAsync)) is { } rejection)
        {
            return ValueTask.FromException(rejection);
        }
        IndexView view;
        TaskCompletionSource completion;
        lock (StateLock)
        {
            if (_disposalCompletion is { } existing)
            {
                return new ValueTask(existing.Task);
            }
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposalCompletion = completion;
            view = _view;
            view.MarkDisposedLocked();
        }
        _ = CompleteDisposalAsync(view, completion);
        return new ValueTask(completion.Task);
    }

    async Task CompleteDisposalAsync(IndexView view, TaskCompletionSource completion)
    {
        try
        {
            await DisposeViewAsync(view).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    async Task DisposeViewAsync(IndexView view)
    {
        var scan = view.Scan;
        var last = view.DetachRegistry();
        if (last)
        {
            lock (StateLock)
            {
                scan.MarkClosingLocked();
            }
        }
        Hooks.DisposedFlagSet?.Invoke();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await _disposalCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        try
        {
            if (last)
            {
                await scan.CloseAsync().ConfigureAwait(false);
            }
            else
            {
                await view.Borrows.CloseAndDrainAsync().ConfigureAwait(false);
            }
        }
        catch (Exception releaseFailure) when (failure is not null)
        {
            throw new AggregateException(failure.SourceException, releaseFailure);
        }
        finally
        {
            await view.CloseDeliveriesAsync().ConfigureAwait(false);
            scan.RemoveView(view);
        }
        failure?.Throw();
    }
}
