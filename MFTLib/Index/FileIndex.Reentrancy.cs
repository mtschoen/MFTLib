namespace MFTLib.Index;

/// <summary>
///     Rejects lifecycle calls made from inside this index's <see cref="Changed" />,
///     <see cref="WatchFaulted" /> or <see cref="WatchStateChanged" /> handlers with
///     <see cref="InvalidOperationException" /> to prevent deadlocks between event delivery
///     and lifecycle work. Queue lifecycle work to run after the handler returns.
///     Work executing after that invocation returns is allowed, including queued work
///     that inherited the handler's execution context.
/// </summary>
public sealed partial class FileIndex
{
    // Every raise site uses Deliver to set an AsyncLocal marker naming this index.
    // The marker flows into awaits and queued work; its Active flag clears when the
    // invocation returns. The outer chain preserves nested deliveries across indexes.
    static readonly AsyncLocal<DeliveryMarker?> Delivery = new();

    /// <summary>Invokes one handler with this index's delivery marker set for the duration of the call.</summary>
    void Deliver<TArgument>(Action<TArgument> handler, TArgument argument)
    {
        var previous = Delivery.Value;
        var marker = new DeliveryMarker(this, previous);
        Delivery.Value = marker;
        try
        {
            handler(argument);
        }
        finally
        {
            marker.End();
            Delivery.Value = previous;
        }
    }

    /// <summary>
    ///     Invokes every subscriber of a multicast event in order, each under its own marker that goes
    ///     inactive when that subscriber returns. A subscriber's exception stops the later ones, as
    ///     a plain multicast invocation would.
    /// </summary>
    void DeliverToEach<TArgument>(Action<TArgument> subscribers, TArgument argument)
    {
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            Deliver((Action<TArgument>)subscriber, argument);
        }
    }

    /// <summary>
    ///     The exception a lifecycle call named <paramref name="operation" /> fails with when it is
    ///     made from inside one of this index's handlers, or null when the call is allowed.
    /// </summary>
    InvalidOperationException? RejectInsideHandler(string operation)
    {
        return IsInsideHandlerOfThisIndex()
            ? new InvalidOperationException(
                $"FileIndex.{operation} was called from inside a Changed, WatchFaulted or WatchStateChanged " +
                "handler; it can wait for a watch pump that is blocked in a handler. Queue the call to run " +
                "after the handler returns, for example with Task.Run.")
            : null;
    }

    /// <summary>
    ///     Whether an active delivery of this index is on the execution context's chain. The chain
    ///     keeps the outer markers, so a handler of another index that a handler of this one
    ///     triggered synchronously is still recognized as inside this index's handler.
    /// </summary>
    bool IsInsideHandlerOfThisIndex()
    {
        for (var marker = Delivery.Value; marker is not null; marker = marker.Outer)
        {
            if (marker.Active && ReferenceEquals(marker.Owner, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An already faulted task for a rejected call, or null when the call is allowed.</summary>
    Task? RejectedTask(string operation) =>
        RejectInsideHandler(operation) is { } rejection ? Task.FromException(rejection) : null;

    /// <summary>The typed counterpart of <see cref="RejectedTask(string)" />.</summary>
    Task<TResult>? RejectedTask<TResult>(string operation) =>
        RejectInsideHandler(operation) is { } rejection ? Task.FromException<TResult>(rejection) : null;

    /// <summary>
    ///     Awaits <paramref name="wait" />, cancelled by either token, without ever running the
    ///     awaiter's continuation inline on the stack that settles the wait or cancels a token.
    ///     The tokens reach the wait by cancelling a completion source created with
    ///     <see cref="TaskCreationOptions.RunContinuationsAsynchronously" /> from a registration,
    ///     not through <see cref="Task.WaitAsync(CancellationToken)" />, which makes no such
    ///     promise. A pump settling a waiter, or a handler cancelling a token, therefore never runs
    ///     the waiter's code on its own thread.
    /// </summary>
    static async Task AwaitQueuedAsync(Task wait, CancellationToken first, CancellationToken second)
    {
        if (wait.IsCompleted || (!first.CanBeCanceled && !second.CanBeCanceled))
        {
            await wait.ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstRegistration = first.Register(
            static (state, token) => (state as TaskCompletionSource)?.TrySetCanceled(token), completion);
        await using var secondRegistration = second.Register(
            static (state, token) => (state as TaskCompletionSource)?.TrySetCanceled(token), completion);
        _ = wait.ContinueWith(antecedent => CopyOutcome(antecedent, completion),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await completion.Task.ConfigureAwait(false);
    }

    static void CopyOutcome(Task antecedent, TaskCompletionSource completion)
    {
        if (antecedent.Exception is { } failure)
        {
            completion.TrySetException(failure.InnerExceptions);
        }
        else if (antecedent.IsCanceled)
        {
            completion.TrySetCanceled();
        }
        else
        {
            completion.TrySetResult();
        }
    }

    sealed class DeliveryMarker(FileIndex owner, DeliveryMarker? outer)
    {
        volatile bool _active = true;

        public DeliveryMarker? Outer { get; } = outer;

        public FileIndex Owner { get; } = owner;

        public bool Active => _active;

        public void End() => _active = false;
    }
}
